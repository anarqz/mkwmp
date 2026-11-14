// Args: output, address:count:kind ...; kind is insn, ptr, or str.
import ghidra.app.script.GhidraScript;
import java.io.PrintWriter;

public class InspectWireAddresses extends GhidraScript {
    public void run() throws Exception {
        String[] args = getScriptArgs();
        try (PrintWriter out = new PrintWriter(args[0])) {
            for (int i = 1; i < args.length; i++) {
                String[] spec = args[i].split(":");
                var addr = toAddr(Long.parseUnsignedLong(spec[0], 16));
                int count = Integer.parseInt(spec[1]);
                for (int j = 0; j < count; j++) {
                    if (spec[2].equals("insn")) {
                        if (getInstructionAt(addr) == null) disassemble(addr);
                        out.printf("%s %s%n", addr, getInstructionAt(addr));
                        addr = addr.add(4);
                    } else if (spec[2].equals("ptr")) {
                        out.printf("%s %016x%n", addr, getLong(addr));
                        addr = addr.add(8);
                    } else {
                        int ch = getByte(addr) & 255;
                        if (ch == 0) break;
                        out.print((char)ch);
                        addr = addr.add(1);
                    }
                }
                out.println();
            }
        }
    }
}
