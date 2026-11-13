// Find short ADRP + LDR/ADD sequences that resolve a known image address.
// args: <output file> <hex address> [hex address ...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.MemoryBlock;
import java.io.PrintWriter;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.HashSet;
import java.util.Set;

public class FindArm64PageRefs extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        Set<Long> wanted = new HashSet<>();
        Set<Long> branches = new HashSet<>();
        Set<Integer> constants = new HashSet<>();
        int field = -1;
        for (int i = 1; i < args.length; i++) {
            if (args[i].startsWith("field:")) field = Integer.parseUnsignedInt(args[i].substring(6), 16);
            else if (args[i].startsWith("branch:")) branches.add(Long.parseUnsignedLong(args[i].substring(7), 16));
            else if (args[i].startsWith("constant:")) constants.add(Integer.parseUnsignedInt(args[i].substring(9), 16));
            else wanted.add(Long.parseUnsignedLong(args[i], 16));
        }
        MemoryBlock block = currentProgram.getMemory().getBlock(toAddr(0));
        if (block == null) throw new IllegalStateException("No raw image at address zero");
        byte[] bytes = getBytes(block.getStart(), (int)Math.min(block.getSize(), 0x2820000));
        ByteBuffer code = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
        try (PrintWriter out = new PrintWriter(args[0])) {
            for (int i = 0; i + 4 <= bytes.length; i += 4) {
                if ((i & 0xffff) == 0) monitor.checkCancelled();
                int w = code.getInt(i);
                if (!constants.isEmpty() && (w & 0xffe00000) == 0x52800000) {
                    int low = (w >>> 5) & 0xffff;
                    for (int k = i + 4; k <= i + 24 && k + 4 <= bytes.length; k += 4) {
                        int upper = code.getInt(k);
                        if ((upper & 0xffe0001f) == (0x72a00000 | (w & 31))) {
                            int value = low | (((upper >>> 5) & 0xffff) << 16);
                            if (constants.contains(value)) out.printf("%08x constant %08x%n", i, value);
                            break;
                        }
                    }
                }
                if ((w & 0x7c000000) == 0x14000000) {
                    long target = block.getStart().getOffset() + i + ((w << 6) >> 4);
                    if (branches.contains(target)) out.printf("%08x branch -> %08x%n", i, target);
                }
                if (field >= 0 && (w & 0xfffffc00) == 0x321c0000) { // ORR Wd, Wn, #0x10
                    for (int k = i + 4; k <= i + 24 && k + 4 <= bytes.length; k += 4) {
                        int store = code.getInt(k);
                        if ((store & 0xffc00000) == 0xb9000000 && (store & 31) == (w & 31) &&
                            ((store >>> 10) & 0xfff) * 4 == field) {
                            out.printf("%08x ORR bit 4 at %08x then field store%n", k, i);
                        }
                    }
                }
                if ((w & 0x9f000000) != 0x90000000) continue;
                long pc = block.getStart().getOffset() + i;
                int imm = (((w >>> 5) & 0x7ffff) << 2) | ((w >>> 29) & 3);
                long page = (pc & ~0xfffL) + (((imm << 11) >> 11) * 4096L);
                int register = w & 31;
                for (int j = i + 4; j <= i + 32 && j + 4 <= bytes.length; j += 4) {
                    int op = code.getInt(j);
                    if (((op >>> 5) & 31) != register) continue;
                    int scaled = (op >>> 10) & 0xfff;
                    long offset;
                    if ((op & 0xff800000) == 0x91000000) offset = (long)scaled << (((op >>> 22) & 1) * 12);
                    else if ((op & 0xffc00000) == 0xf9400000) offset = scaled * 8L;
                    else continue;
                    long target = page + offset;
                    if (wanted.contains(target)) {
                        Address use = toAddr(block.getStart().getOffset() + j);
                        if (field < 0) out.printf("%08x -> %08x (ADRP %08x)%n", use.getOffset(), target, pc);
                        else {
                            int registers = 1 << (op & 31);
                            for (int k = j + 4; k <= j + 64 && k + 4 <= bytes.length; k += 4) {
                                int store = code.getInt(k);
                                int base = (store >>> 5) & 31;
                                if ((registers & (1 << base)) == 0) continue;
                                if ((store & 0xfffffc00) == 0xf9400000) registers |= 1 << (store & 31);
                                if ((store & 0xffc00000) == 0xb9000000 &&
                                    (((store >>> 10) & 0xfff) * 4) == field) {
                                    out.printf("%08x store after %08x -> %08x%n", block.getStart().getOffset() + k,
                                               use.getOffset(), target);
                                }
                            }
                        }
                    }
                    if ((op & 31) == register) break;
                }
            }
        }
    }
}
