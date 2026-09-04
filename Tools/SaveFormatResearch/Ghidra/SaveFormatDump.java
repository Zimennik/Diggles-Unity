// Dump targeted save-format functions with instructions, references, callers, and decompiled C.
//
// Script arguments:
//   1. Output file path.
//   2+. Function entry addresses.
//
//@category Diggles
//@author Diggles RE
//@runtime Java

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

import java.io.BufferedWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

public class SaveFormatDump extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            throw new IllegalArgumentException(
                    "Expected an output path followed by one or more function addresses.");
        }

        Path outputPath = Paths.get(args[0]);
        Path parent = outputPath.getParent();
        if (parent != null) {
            Files.createDirectories(parent);
        }

        DecompInterface decompiler = new DecompInterface();
        decompiler.setSimplificationStyle("decompile");
        decompiler.openProgram(currentProgram);

        try (BufferedWriter writer = Files.newBufferedWriter(
                outputPath, StandardCharsets.UTF_8)) {
            for (int i = 1; i < args.length; i++) {
                Address address = toAddr(args[i]);
                Function function = getFunctionAt(address);
                if (function == null) {
                    writer.write("MISSING " + address + "\n\n");
                    continue;
                }

                writer.write("FUNCTION " + function.getEntryPoint() + " "
                        + function.getName(true) + "\n");
                writer.write("PROTOTYPE " + function.getPrototypeString(true, true) + "\n");

                writer.write("CALLERS\n");
                ReferenceIterator callers = currentProgram.getReferenceManager()
                        .getReferencesTo(function.getEntryPoint());
                while (callers.hasNext()) {
                    Reference reference = callers.next();
                    Function caller = getFunctionContaining(reference.getFromAddress());
                    writer.write("  " + reference.getFromAddress() + " "
                            + reference.getReferenceType() + " "
                            + (caller == null ? "<no function>" : caller.getName(true)) + "\n");
                }

                writer.write("INSTRUCTIONS\n");
                InstructionIterator instructions = currentProgram.getListing()
                        .getInstructions(function.getBody(), true);
                while (instructions.hasNext()) {
                    Instruction instruction = instructions.next();
                    writer.write("  " + instruction.getAddress() + "  "
                            + instruction.toString() + "\n");
                    for (Reference reference : instruction.getReferencesFrom()) {
                        writer.write("      -> " + reference.getToAddress() + " "
                                + reference.getReferenceType() + "\n");
                    }
                }

                writer.write("DECOMPILED\n");
                DecompileResults result = decompiler.decompileFunction(function, 60, monitor);
                if (result != null && result.decompileCompleted()) {
                    writer.write(result.getDecompiledFunction().getC());
                }
                else {
                    writer.write("<decompilation failed>\n");
                }
                writer.write("\n\n");
            }
        }
        finally {
            decompiler.dispose();
        }

        println("Save-format dump written to " + outputPath);
    }
}
