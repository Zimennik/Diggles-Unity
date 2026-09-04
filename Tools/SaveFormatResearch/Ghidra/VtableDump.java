// Dump pointer-sized entries from one or more 32-bit vtables.
//
// Script arguments:
//   1. Output file path.
//   2. Number of entries per vtable.
//   3+. Vtable addresses.
//
//@category Diggles
//@author Diggles RE
//@runtime Java

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

import java.io.BufferedWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

public class VtableDump extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3) {
            throw new IllegalArgumentException(
                    "Expected an output path, entry count, and one or more vtable addresses.");
        }

        Path outputPath = Paths.get(args[0]);
        Path parent = outputPath.getParent();
        if (parent != null) {
            Files.createDirectories(parent);
        }

        int entryCount = Integer.parseInt(args[1]);
        if (entryCount <= 0) {
            throw new IllegalArgumentException("The vtable entry count must be positive.");
        }

        try (BufferedWriter writer = Files.newBufferedWriter(
                outputPath, StandardCharsets.UTF_8)) {
            for (int argumentIndex = 2; argumentIndex < args.length; argumentIndex++) {
                Address tableAddress = toAddr(args[argumentIndex]);
                writer.write("VTABLE " + tableAddress + "\n");
                for (int entryIndex = 0; entryIndex < entryCount; entryIndex++) {
                    Address entryAddress = tableAddress.add(entryIndex * 4L);
                    long pointerValue = Integer.toUnsignedLong(getInt(entryAddress));
                    Address targetAddress = toAddr(pointerValue);
                    Function function = getFunctionAt(targetAddress);
                    writer.write(String.format(
                            "  +0x%02X %s %s\n",
                            entryIndex * 4,
                            targetAddress,
                            function == null ? "<no function>" : function.getName(true)));
                }
                writer.write("\n");
            }
        }

        println("Vtable dump written to " + outputPath);
    }
}
