#nullable enable

namespace Diggles.Runtime.World
{
    using System.IO;

    public sealed class BdbAtlas
    {
        public const int SubGridSide = 8;
        public const int SubGridLen = SubGridSide * SubGridSide;
        public const int AuxBytesPerBlock = 89;

        public uint NBlocks { get; init; }
        public byte[] Aux { get; init; } = System.Array.Empty<byte>();

        public byte HeightAt(int blockIndex, int sx, int sy)
        {
            if (blockIndex < 0 || blockIndex >= (int)this.NBlocks) return 0;
            return this.Aux[blockIndex * AuxBytesPerBlock + sy * SubGridSide + sx];
        }

        public static BdbAtlas? TryLoadFrom(string dataRoot, string fileName)
        {
            var path = Path.Combine(dataRoot, fileName);
            if (!File.Exists(path)) return null;
            try
            {
                var model = BdbParser.Parse(path);
                return new BdbAtlas
                {
                    NBlocks = model.Header.NBlocks,
                    Aux = model.PerBlockAux,
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
