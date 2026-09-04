#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSaveAiOwnerDocument
    {
        private readonly ReadOnlyCollection<OriginalSaveTclVariable> _variables;

        internal OriginalSaveAiOwnerDocument(
            bool enabled,
            int owner,
            OriginalSaveNativeString scriptName,
            bool interpreterPresent,
            OriginalSaveTclVariable[] variables)
        {
            Enabled = enabled;
            Owner = owner;
            ScriptName = scriptName ?? throw new ArgumentNullException(nameof(scriptName));
            InterpreterPresent = interpreterPresent;
            _variables = Array.AsReadOnly(
                variables ?? throw new ArgumentNullException(nameof(variables)));
        }

        public bool Enabled { get; }
        public int Owner { get; }
        public OriginalSaveNativeString ScriptName { get; }
        public bool InterpreterPresent { get; }
        public IReadOnlyList<OriginalSaveTclVariable> Variables => _variables;
    }

    public sealed class OriginalSaveAiDocument
    {
        public const string NativeStandardScriptPath = "data/scripts/ai/std_ai.tcl";

        private const int OwnerCount = 8;
        private const int MaximumStringLength = 1024 * 1024;
        private const int MaximumVariableCount = 4 * 1024 * 1024;

        private readonly ReadOnlyCollection<OriginalSaveAiOwnerDocument> _owners;

        internal OriginalSaveAiDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.AiOwnerStateOffset);
            int end = checked((int)metadata.AiOwnerStateEndOffset);
            var cursor = new Cursor(sourcePayload, start, end);
            var owners = new OriginalSaveAiOwnerDocument[OwnerCount];
            for (int owner = 0; owner < owners.Length; owner++)
            {
                bool enabled = cursor.ReadBooleanByte("AI enabled flag");
                int serializedOwner = cursor.ReadInt32();
                if (serializedOwner != owner)
                {
                    throw new InvalidDataException(
                        $"Native AI slot {owner} contains owner {serializedOwner}.");
                }
                OriginalSaveNativeString scriptName = cursor.ReadNativeString();
                bool interpreterPresent = cursor.ReadBooleanInt32("AI Tcl interpreter");
                OriginalSaveTclVariable[] variables = interpreterPresent
                    ? cursor.ReadVariables()
                    : Array.Empty<OriginalSaveTclVariable>();
                owners[owner] = new OriginalSaveAiOwnerDocument(
                    enabled,
                    serializedOwner,
                    scriptName,
                    interpreterPresent,
                    variables);
            }

            if (cursor.Position != end)
            {
                throw new InvalidDataException(
                    $"Typed AI parsing ended at {cursor.Position}, expected {end}.");
            }
            _owners = Array.AsReadOnly(owners);
        }

        public IReadOnlyList<OriginalSaveAiOwnerDocument> Owners => _owners;

        private sealed class Cursor
        {
            private readonly byte[] _source;
            private readonly int _end;

            internal Cursor(byte[] source, int start, int end)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
                if (start < 0 || end < start || end > source.Length)
                    throw new ArgumentOutOfRangeException(nameof(start));
                Position = start;
                _end = end;
            }

            internal int Position { get; private set; }

            internal bool ReadBooleanByte(string label)
            {
                EnsureAvailable(sizeof(byte));
                byte value = _source[Position++];
                if (value > 1) throw new InvalidDataException($"Invalid {label} value {value}.");
                return value != 0;
            }

            internal bool ReadBooleanInt32(string label)
            {
                int value = ReadInt32();
                if ((uint)value > 1)
                    throw new InvalidDataException($"Invalid {label} presence value {value}.");
                return value != 0;
            }

            internal int ReadInt32()
            {
                EnsureAvailable(sizeof(int));
                int value = OriginalSaveWorldDocument.ReadInt32(_source, Position);
                Position += sizeof(int);
                return value;
            }

            internal OriginalSaveTclVariable[] ReadVariables()
            {
                int count = ReadInt32();
                if (count < 0 || count > MaximumVariableCount)
                    throw new InvalidDataException($"Invalid native AI variable count {count}.");
                var variables = new OriginalSaveTclVariable[count];
                for (int index = 0; index < variables.Length; index++)
                {
                    variables[index] = new OriginalSaveTclVariable(
                        ReadNativeString().Text,
                        ReadNativeString().Text);
                }
                return variables;
            }

            internal OriginalSaveNativeString ReadNativeString()
            {
                int length = ReadInt32();
                if (length <= 0 || length > MaximumStringLength)
                    throw new InvalidDataException($"Invalid native AI string length {length}.");
                EnsureAvailable(length);
                var bytes = new byte[length];
                Buffer.BlockCopy(_source, Position, bytes, 0, length);
                Position += length;
                if (bytes[^1] != 0)
                    throw new InvalidDataException("Native AI string is not NUL-terminated.");
                return new OriginalSaveNativeString(bytes);
            }

            private void EnsureAvailable(int size)
            {
                if (size < 0 || (long)Position + size > _end)
                    throw new EndOfStreamException("Unexpected end of native AI owner state.");
            }
        }
    }
}
