// Headless decompile of the functions containing given addresses, WITHOUT full auto-analysis.
// For each addr: scan back for the `pacibsp` prologue (this binary uses PAC) to find the function
// start, disassemble, create the function, and decompile.
// args: <outfile> <hexaddr> [hexaddr ...] (image-relative; prefix entry: for a known entry point)
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSpace;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.util.task.ConsoleTaskMonitor;
import java.io.FileWriter;
import java.io.PrintWriter;

public class DecompAddrs extends GhidraScript {
    private Address findStart(Address a) {
        for (int off = 0; off < 0x6000; off += 4) {
            try {
                Address p = a.subtract(off);
                int w = getInt(p);
                if (w == 0xD503237F) return p;   // pacibsp
            } catch (Exception e) { break; }
        }
        return a;
    }

    public void run() throws Exception {
        String[] args = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(args[0]));
        AddressSpace sp = currentProgram.getAddressFactory().getDefaultAddressSpace();
        FunctionManager fm = currentProgram.getFunctionManager();
        DecompInterface dec = new DecompInterface();
        dec.toggleCCode(true);
        dec.openProgram(currentProgram);
        ConsoleTaskMonitor mon = new ConsoleTaskMonitor();

        for (int i = 1; i < args.length; i++) {
            boolean exact = args[i].startsWith("entry:");
            Address a = sp.getAddress(Long.parseLong(exact ? args[i].substring(6) : args[i], 16));
            Function fn = exact ? fm.getFunctionAt(a) : fm.getFunctionContaining(a);
            if (fn == null) {
                Address start = exact ? a : findStart(a);
                try { if (getInstructionAt(start) == null) disassemble(start); } catch (Exception e) {}
                try { fn = createFunction(start, null); } catch (Exception e) {}
                if (fn == null && !exact) fn = fm.getFunctionContaining(a);
            }
            if (fn == null) { out.println("// " + args[i] + " : NO FUNCTION\n"); continue; }
            out.println("// ==== addr " + args[i] + "  " + fn.getName() + " @ " + fn.getEntryPoint() + " ====");
            DecompileResults r = dec.decompileFunction(fn, 120, mon);
            if (r != null && r.decompileCompleted()) out.println(r.getDecompiledFunction().getC());
            else out.println("// decompile failed: " + (r != null ? r.getErrorMessage() : "none"));
            out.println();
        }
        out.close();
        println("DecompAddrs wrote " + args[0]);
    }
}

