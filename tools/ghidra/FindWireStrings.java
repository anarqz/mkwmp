// List matching ASCII strings from the raw image. Args: output, regex.
import ghidra.app.script.GhidraScript;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;
import java.util.regex.Pattern;

public class FindWireStrings extends GhidraScript {
    public void run() throws Exception {
        String[] args = getScriptArgs();
        Pattern pattern = Pattern.compile(args[1]);
        var block = currentProgram.getMemory().getBlock(toAddr(0));
        byte[] bytes = getBytes(block.getStart(), (int)block.getSize());
        try (PrintWriter out = new PrintWriter(args[0])) {
            int start = 0;
            for (int i = 0; i < bytes.length; i++) {
                if (bytes[i] >= 32 && bytes[i] <= 126) continue;
                if (i - start >= 5 && i - start < 1024) {
                    String value = new String(bytes, start, i - start, StandardCharsets.US_ASCII);
                    if (pattern.matcher(value).find()) out.printf("%08x %s%n", start, value);
                }
                start = i + 1;
            }
        }
    }
}
