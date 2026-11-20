// Crash records: what the game process leaves behind when it dies, so a crash in the field says where it happened.
//
// One record per fatal event, appended to <game folder>\mkwmp-crash.log (never truncated; see "size" below), and a
// minidump beside it, <game folder>\mkwmp-crash-<id>.dmp. The MKWMP launcher finds both after the game ends and
// offers to report them. Windows only (also under Wine/Proton); elsewhere every function here does nothing, and the
// runtime's own handler (logs/mkw.log) is all there is.
//
// ---- what is watched, and why none of it changes what the game does with its own exceptions
// mkw-recomp uses access violations for real work: guest memory is committed on first touch and frozen regions are
// thawed by a vectored handler (runtime/platform/win32_mem.c, fault_veh) that returns EXCEPTION_CONTINUE_EXECUTION.
// Its crash reporter (crash_veh) ends the process itself, ExitProcess(71), for a fault in mkw.exe, mkwgame.dll or at
// an address in no module; faults anywhere else (drivers, system libraries, ours) it leaves alone. Guest setjmp /
// longjmp restore registers and raise nothing. So:
//
//   1. A vectored handler, first in line, for fatal codes only (access violation, illegal / privileged instruction,
//      integer divide, stack overflow, in-page error, heap corruption, fail-fast, and MKWMP_CRASH_EXCEPTION). It
//      ALWAYS returns EXCEPTION_CONTINUE_SEARCH. It writes the record at once only when nothing can still take the
//      exception: heap corruption, fail-fast, ours, and a fault the runtime's reporter is about to end the process
//      for (in mkw.exe / mkwgame.dll / no module, and not an access violation its fault handler can still repair,
//      i.e. one whose target is neither reserved-but-uncommitted memory nor a read-only page being written). Any
//      other fault is only remembered: a copy of the exception record and the context, a few hundred nanoseconds.
//   2. The unhandled-exception filter (chained: the filter there before runs after ours, and ours is put back on
//      top if something replaces it). The last chance for a fault no frame handled: drivers, system libraries, our
//      own libraries. This is where the record of the remembered fault is written.
//   3. Process exit: if the process ends while the calling thread's remembered fault is still unresolved (its
//      target still cannot be accessed), the runtime's reporter took it and called ExitProcess; the record is written
//      then, from the remembered context (stage "exit": the other threads are gone, so the dump has this thread only).
//   4. abort() (SIGABRT), the C runtime's invalid-parameter handler (chained), and std::terminate in
//      mkwmp-voice.dll (which raises MKWMP_CRASH_EXCEPTION with its reason).
//
// A record is written once per event (the stages above share one memory of what was recorded), at most
// CRASH_MAX_PER_SESSION per session. Nothing in the fault path allocates: fixed buffers, a formatter of its own,
// WriteFile. The writing is done by a thread started at install with its own stack, so a stack overflow is recorded
// too; the faulting thread waits for it (it writes inline when that thread is gone: process exit).
//
// What __fastfail, a kill from outside and a hang leave: nothing here. The launcher still sees the exit code.
//
// ---- size
// mkwmp-crash.log is append-only. At install, when it is over CRASH_LOG_CAP, every record but the newest
// CRASH_LOG_KEEP_FULL is cut down to its three summary lines (id, summary, end): none is ever lost from the list.
// At most CRASH_MAX_DUMPS dumps are kept (the oldest go); a dump over CRASH_DUMP_CAP is written again without the
// referenced memory.
//
// ---- the record (docs/contracts in the launcher: Mkwmp.Core/Diagnostics/CrashRecord.cs reads exactly this)
//   === MKWMP CRASH <id> ===                         id = <UTC yyyymmddThhmmssZ>-<pid>[-<n>]
//   summary: <CODE_NAME> in <module>+0x<off> (players=<n|~n|?>, host=<1|0|?>, mesh=<created|joined|?>)
//   time_utc: 2026-11-20T18:32:45.123Z
//   uptime_ms: <session uptime>      stage: first-chance|unhandled|exit|abort|invalid-parameter
//   pid: <pid>   tid: <thread>
//   exception: 0x<code> <CODE_NAME>
//   address: 0x<rip> <module>+0x<off>
//   access: read|write|execute 0x<target>            (access violations and in-page errors)
//   reason: <text>                                   (MKWMP_CRASH_EXCEPTION, abort, invalid parameter)
//   os: ...    gpu: ...                              (taken at install)
//   state:                                           the breadcrumbs' named values, one "  key=value" a line:
//     mkwmp= loader= recomp= game= name= account= lobby= online= mesh= ...
//   room: lobby=<uuid|?> ussid=<n|?> uid=<uuid|?> host=<1|0|?> players=<n|~n|?> ussids=<a,b,c|?> age_ms=<n|?>
//         the lobby's own numbers (its log: "room order lobby=<uuid> ... by_ussid=3*:<uid>,..."); host=1 when ours
//         is the lowest ussid. uid is always there once the player's name is known (uuid5 of it, as the lobby
//         derives it); the rest only when the seat reply carried a "room" object (seat.c), else "?" and players
//         is "~n" from the Pia endpoints or "?". age_ms: how long before the crash the room was last told.
//   peers: <n>                                       remote Pia endpoints with traffic in the last 15 s
//   module: <name> base=0x.. size=0x.. time=0x<PE timestamp>     (mkw.exe, mkwgame.dll, ours, the faulting one)
//   registers:  rax=.. (four a line)
//   stack:      "  #NN <module>+0x<off>"  (RtlVirtualUnwind from the fault's context)
//   guest: cpu=0x.. pc=<main+0x..> lr=.. sp=.. fp=..   (a Cpu found in the host registers; pc is the last
//   guest_stack: "  g#NN main+0x.."                     indirect call's target, lr and the frames are exact)
//   events:     "  [<uptime ms>] hh:mm:ss.mmmZ t<tid> <text>"  the last 50 breadcrumbs, oldest first (UTC, same day
//               as time_utc or the one before); a room change reads "room: lobby=.. ussid=.. host=.. players=.. ussids=.."
//   dump: mkwmp-crash-<id>.dmp <bytes> bytes | none (<why>)
//   === END <id> ===
#ifndef MKWMP_CRASH_H
#define MKWMP_CRASH_H

#include <stdint.h>

enum {
    CRASH_MAX_PER_SESSION = 4,
    CRASH_MAX_DUMPS = 3,
    CRASH_LOG_KEEP_FULL = 8,
};
#define CRASH_LOG_CAP (1u << 20)
#define CRASH_DUMP_CAP (64u << 20)

// Raised (RaiseException, noncontinuable) by code of ours that is about to end the process without a fault: recorded at
// once. ExceptionInformation[0] is a const char * reason (may be 0). mkwmp-voice.dll's terminate handler uses it; it
// keeps its own copy of this number (native/voice/src/mkwmp_voice.cpp).
#define MKWMP_CRASH_EXCEPTION 0xE04D4B57u

// Install the handlers. Idempotent. `dir` is where the record and the dumps go (UTF-8); NULL is the game's folder
// (mkw.exe's). `before_runtime` is 1 when this runs before mkw.exe's own start-up (the winmm.dll proxy's DllMain): the
// runtime's fault handler then comes ahead of ours, and every access violation ours sees is one it declined.
// From DllMain it only registers the handlers and starts the writer thread; the rest (system facts, dbghelp, the
// size cap) is that thread's.
void crash_install(const char *dir, int before_runtime);

// The runtime's "guest Cpu of the calling thread" (kern_cpu), when the loader can resolve it: asked on the faulting
// thread. Without it a Cpu is looked for in the host registers of the fault and of the frames above it.
void crash_set_guest_cpu(void *(*current)(void));

// Tests: the id of the last record written by this process ("" if none), and whether the writer thread is ready
// (dbghelp loaded, the system facts taken).
const char *crash_last_id(void);
int crash_ready(void);

#endif
