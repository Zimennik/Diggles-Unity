#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Tcl;

    public enum ProductionSlotOverride
    {
        Normal = 0,
        Disabled = 1,
        Hidden = 2,
    }

    // Global production-window overrides. The native game stores 32 integers at
    // CGame+0xbc (FUN_0055a7c0/FUN_00524de0). Product-list positions use 0..30;
    // position 31 is the shared pack/unpack/cancel cell (FUN_00521c80:645-683).
    public sealed class ProductionSlotOverrides
    {
        public const int SlotCount = 32;
        public const int PackSlotIndex = 31;

        readonly ProductionSlotOverride[] _states = new ProductionSlotOverride[SlotCount];

        public ProductionSlotOverride this[int index]
        {
            get
            {
                ValidateIndex(index);
                return _states[index];
            }
            set
            {
                ValidateIndex(index);
                if (value < ProductionSlotOverride.Normal || value > ProductionSlotOverride.Hidden)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _states[index] = value;
            }
        }

        public void Reset() => Array.Clear(_states, 0, _states.Length);

        public int[] Capture()
        {
            var result = new int[SlotCount];
            for (int i = 0; i < result.Length; i++) result[i] = (int)_states[i];
            return result;
        }

        public void Restore(IReadOnlyList<int> values)
        {
            Validate(values);
            for (int i = 0; i < values.Count; i++)
                _states[i] = (ProductionSlotOverride)values[i];
        }

        public static void Validate(IReadOnlyList<int>? values)
        {
            if (values == null || values.Count != SlotCount)
                throw new ArgumentException("Production-slot override table must contain 32 entries.", nameof(values));
            for (int index = 0; index < values.Count; index++)
            {
                if (values[index] < 0 || values[index] > 2)
                    throw new ArgumentException("Production-slot override values must be in range 0..2.", nameof(values));
            }
        }

        public static void RegisterCommands(DigglesTclContext context, TclInterpreter interpreter)
        {
            interpreter.RegisterCommand("prodslot_override", (_, args) =>
            {
                if (args.Length < 2)
                    throw new TclException("wrong # args: should be \"prodslot_override option ?index?\"");

                string option = args[1];
                if (string.Equals(option, "reset", StringComparison.OrdinalIgnoreCase))
                {
                    // The original handles reset before its three-argument gate and consequently
                    // ignores trailing arguments. Preserve that harmless native quirk.
                    context.ProdSlotOverrides.Reset();
                    return "";
                }

                if (args.Length != 3)
                    throw new TclException("wrong # args: should be \"prodslot_override option index\"");
                if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                    || index < 0 || index >= SlotCount)
                    throw new TclException("invalid index");

                if (string.Equals(option, "normal", StringComparison.OrdinalIgnoreCase))
                    context.ProdSlotOverrides[index] = ProductionSlotOverride.Normal;
                else if (string.Equals(option, "disable", StringComparison.OrdinalIgnoreCase))
                    context.ProdSlotOverrides[index] = ProductionSlotOverride.Disabled;
                else if (string.Equals(option, "hide", StringComparison.OrdinalIgnoreCase))
                    context.ProdSlotOverrides[index] = ProductionSlotOverride.Hidden;
                else if (string.Equals(option, "query", StringComparison.OrdinalIgnoreCase))
                    return ((int)context.ProdSlotOverrides[index]).ToString(CultureInfo.InvariantCulture);
                else
                    throw new TclException("unknown sub-command");
                return "";
            });
        }

        static void ValidateIndex(int index)
        {
            if (index < 0 || index >= SlotCount) throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
