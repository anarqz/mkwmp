"""Read the connection controller and its completion ring in this MKW build."""

import argparse
import ctypes as C
import json
import struct
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pid", type=int)
    parser.add_argument("--seconds", type=float, default=0)
    args = parser.parse_args()
    kernel = C.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [C.c_ulong, C.c_int, C.c_ulong]
    kernel.OpenProcess.restype = C.c_void_p
    kernel.ReadProcessMemory.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p, C.c_size_t, C.POINTER(C.c_size_t)]
    kernel.CloseHandle.argtypes = [C.c_void_p]
    process = kernel.OpenProcess(0x10, False, args.pid)
    if not process:
        raise C.WinError(C.get_last_error())

    def read(address, count):
        buf = C.create_string_buffer(count)
        done = C.c_size_t()
        if not kernel.ReadProcessMemory(process, address, buf, count, C.byref(done)) or done.value != count:
            raise C.WinError(C.get_last_error())
        return buf.raw

    def pointer(address):
        return struct.unpack("<Q", read(address, 8))[0]

    end = time.monotonic() + args.seconds
    previous = None
    try:
        while True:
            try:
                system = pointer(pointer(0x70032f29f8))
                controller = pointer(system + 0xd8) if system else 0
            except OSError as exc:
                if exc.winerror not in (299, 998) or time.monotonic() >= end:
                    raise
                controller = 0
            if not controller:
                if time.monotonic() >= end:
                    parser.exit(1, "Connection controller was not initialized.\n")
                time.sleep(0.05)
                continue
            area = read(controller, 0x4b6)
            mode, phase = struct.unpack_from("<II", area, 8)
            job, operation, operation_mode = struct.unpack_from("<III", area, 0x468)
            ring, capacity, head, count = struct.unpack_from("<QIII", area, 0x490)
            desired_mode, desired_phase, desired_slot = struct.unpack_from("<III", area, 0x4a8)
            global_flags = pointer(0x70033d2548)
            services = pointer(0x70033dc800)
            error = pointer(services + 0x28) if services else 0
            error_words = struct.unpack("<7I", read(error + 8, 28)) if error else None
            entries = []
            if ring and 0 < capacity <= 64:
                raw = read(ring, capacity * 0x28)
                for index in range(min(count, capacity)):
                    pos = ((head + index) % capacity) * 0x28
                    entries.append(struct.unpack_from("<III", raw, pos))
            state = dict(mode=mode, phase=phase, job=job, operation=operation,
                         operation_mode=operation_mode, flags=list(area[0x4b4:0x4b6]),
                         desired=[desired_mode, desired_phase, desired_slot],
                         global_flags=struct.unpack("<I", read(global_flags + 0x118, 4))[0] if global_flags else None,
                         error_words=error_words,
                         completions=entries)
            if state != previous:
                print(time.strftime("%H:%M:%S"), json.dumps(state), flush=True)
                previous = state
            if time.monotonic() >= end:
                break
            time.sleep(0.015)
    finally:
        kernel.CloseHandle(process)


if __name__ == "__main__":
    main()
