#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.IO;
    using UnityEngine.Scripting;

    public interface IOriginalSaveRepository
    {
        OriginalSaveArchiveDocument Read(string path);
        void WriteAtomic(string path, OriginalSaveArchiveDocument document);
    }

    // Filesystem boundary for native saves. Mapping and live-world mutation stay in the application
    // service; this repository only validates archives and commits complete files atomically.
    public sealed class OriginalSaveRepository : IOriginalSaveRepository
    {
        private readonly IOriginalSaveArchiveCodec _codec;

        [Preserve]
        public OriginalSaveRepository(IOriginalSaveArchiveCodec codec)
        {
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }

        public OriginalSaveArchiveDocument Read(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            return _codec.Read(path);
        }

        public void WriteAtomic(string path, OriginalSaveArchiveDocument document)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (document == null) throw new ArgumentNullException(nameof(document));

            string destination = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(destination)
                ?? throw new ArgumentException("Save path has no parent directory.", nameof(path));
            Directory.CreateDirectory(directory);

            string temporary = Path.Combine(
                directory,
                $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var file = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough))
                {
                    _codec.Write(file, document, true);
                    file.Flush(true);
                }

                // Do not replace a good save with a truncated or otherwise unreadable archive.
                _codec.Read(temporary);

                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
