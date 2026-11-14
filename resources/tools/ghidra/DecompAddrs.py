# Ghidra headless postScript: decompile the functions containing each given address.
# args: <outfile> <addr1> [addr2 ...]   (addresses are hex, image-relative since base=0)
from ghidra.app.decompiler import DecompInterface
from ghidra.util.task import ConsoleTaskMonitor

args = getScriptArgs()
outfile = args[0]
fm = currentProgram.getFunctionManager()
listing = currentProgram.getListing()
space = currentProgram.getAddressFactory().getDefaultAddressSpace()
dec = DecompInterface()
dec.openProgram(currentProgram)
monitor = ConsoleTaskMonitor()

out = open(outfile, "w")
for a in args[1:]:
    addr = space.getAddress(long(a, 16))
    if listing.getInstructionAt(addr) is None:
        try:
            disassemble(addr)
        except:
            pass
    fn = fm.getFunctionContaining(addr)
    if fn is None:
        try:
            fn = createFunction(addr, None)
        except:
            fn = None
    if fn is None:
        out.write("// ==== %s : NO FUNCTION ====\n\n" % a)
        continue
    out.write("// ==== addr %s  function %s @ %s ====\n" % (a, fn.getName(), fn.getEntryPoint()))
    res = dec.decompileFunction(fn, 90, monitor)
    if res is not None and res.decompileCompleted():
        out.write(res.getDecompiledFunction().getC())
    else:
        out.write("// decompile failed: %s\n" % (res.getErrorMessage() if res else "none"))
    out.write("\n\n")
out.close()
print("DecompAddrs wrote " + outfile)
