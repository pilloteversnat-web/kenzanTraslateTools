using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KenzanMSG
{
    public class File
    {
        public string GameName { get; set; }
        public string Path { get; set; }
        public string ChangesFolder { get; set; }
        public Encoding FileEncoding { get; set; }

        public File(string gameName, string path, string changesFolder, Encoding encoding)
        {
            GameName = gameName;
            Path = path;
            ChangesFolder = changesFolder;
            FileEncoding = encoding;
        }
        public class Subtitle
        {
            public long HeaderPosition { get; set; } // 💡 Añadimos esta propiedad que faltaba
            public long Offset { get; set; }
            public string Text { get; set; }
            public string Translation { get; set; }
            public bool Loaded { get; set; }
        }


        public List<Subtitle> GetSubtitles()
        {
            var result = new List<Subtitle>();

            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(fs, FileEncoding))
            {
                if (fs.Length < 0x200) return result;
                long length = fs.Length;

                for (long i = 0x18; i < length - 4; i++)
                {
                    fs.Seek(i, SeekOrigin.Begin);
                    ushort possibleStrLen = ReadUInt16BigEndian(reader);

                    if (possibleStrLen >= 2 && possibleStrLen < 1500)
                    {
                        if (i + 12 < length)
                        {
                            fs.Seek(i + 4, SeekOrigin.Begin);
                            int strOffset = ReadInt32BigEndian(reader);

                            if (strOffset >= 0x200 && strOffset < length && !result.Any(s => s.Offset == strOffset))
                            {
                                if (IsAValidStringStart(fs, strOffset))
                                {
                                    string text = ReadNullTerminatedString(reader, strOffset, fs);

                                    if (!string.IsNullOrEmpty(text) && text.Length >= 2 && IsValidText(text))
                                    {
                                        result.Add(new Subtitle
                                        {
                                            HeaderPosition = i,
                                            Offset = strOffset,
                                            Text = text,
                                            Translation = text,
                                            Loaded = true
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        private bool IsAValidStringStart(FileStream fs, long offset)
        {
            try
            {
                if (offset <= 0x200) return false;
                fs.Seek(offset - 1, SeekOrigin.Begin);
                byte prevByte = (byte)fs.ReadByte();
                return prevByte == 0x00;
            }
            catch
            {
                return false;
            }
        }

        private string ReadNullTerminatedString(BinaryReader reader, long offset, FileStream fs)
        {
            try
            {
                if (offset <= 0 || offset >= fs.Length) return string.Empty;

                var returnPos = fs.Position;
                fs.Seek(offset, SeekOrigin.Begin);
                var bytes = new List<byte>();

                while (fs.Position < fs.Length)
                {
                    byte b = reader.ReadByte();
                    if (b == 0) break;
                    bytes.Add(b);
                }

                fs.Seek(returnPos, SeekOrigin.Begin);
                return FileEncoding.GetString(bytes.ToArray());
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool IsValidText(string text)
        {
            int validChars = text.Count(c => char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c));
            return (double)validChars / text.Length > 0.5;
        }

        public void RebuildWithSubtitles(string outputFolder, List<Subtitle> subtitles)
        {
            var outputPath = System.IO.Path.Combine(outputFolder, System.IO.Path.GetFileName(Path));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

            if (subtitles == null || subtitles.Count == 0)
            {
                if (Path != outputPath)
                    System.IO.File.Copy(Path, outputPath, true);
                return;
            }

            byte[] originalBytes;
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                originalBytes = new byte[fs.Length];
                fs.Read(originalBytes, 0, (int)fs.Length);
            }

            int dataStartOffset = 0x200;
            var newFileStream = new MemoryStream();
            newFileStream.Write(originalBytes, 0, dataStartOffset);

            var sortedSubtitles = subtitles.Where(s => s.Offset >= 0x200).OrderBy(s => s.Offset).ToList();

            var headerUpdates = new Dictionary<long, (int newOffset, ushort newLength)>();

            foreach (var sub in sortedSubtitles)
            {
                string textToUse = !string.IsNullOrEmpty(sub.Translation) ? sub.Translation : sub.Text;
                byte[] textBytes = FileEncoding.GetBytes(textToUse);

                int newOffset = (int)newFileStream.Position;
                ushort newLength = (ushort)(textBytes.Length + 1);

                headerUpdates[sub.HeaderPosition] = (newOffset, newLength);

                newFileStream.Write(textBytes, 0, textBytes.Length);
                newFileStream.WriteByte(0x00);
            }

            while (newFileStream.Length % 4 != 0)
            {
                newFileStream.WriteByte(0x00);
            }

            byte[] rebuiltBytes = newFileStream.ToArray();

            // Copiamos la cabecera original estructural de forma segura
            Array.Copy(originalBytes, 0, rebuiltBytes, 0, Math.Min(originalBytes.Length, dataStartOffset));

            foreach (var kvp in headerUpdates)
            {
                long headerPos = kvp.Key;
                int newOffset = kvp.Value.newOffset;
                ushort newLen = kvp.Value.newLength;

                // Validación de seguridad para no escribir fuera de rango en la cabecera
                if (headerPos + 7 < rebuiltBytes.Length)
                {
                    // Asignación directa Big-Endian de 2 bytes para la longitud
                    rebuiltBytes[headerPos] = (byte)(newLen >> 8);
                    rebuiltBytes[headerPos + 1] = (byte)(newLen & 0xFF);

                    // Asignación directa Big-Endian de 4 bytes para el offset
                    rebuiltBytes[headerPos + 4] = (byte)(newOffset >> 24);
                    rebuiltBytes[headerPos + 5] = (byte)(newOffset >> 16);
                    rebuiltBytes[headerPos + 6] = (byte)(newOffset >> 8);
                    rebuiltBytes[headerPos + 7] = (byte)(newOffset & 0xFF);
                }
            }

            string tempPath = outputPath + ".tmp";
            if (System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }

            System.IO.File.WriteAllBytes(tempPath, rebuiltBytes);

            if (System.IO.File.Exists(outputPath))
            {
                System.IO.File.Delete(outputPath);
            }
            System.IO.File.Move(tempPath, outputPath);
        }

        private ushort ReadUInt16BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(2);
            if (bytes.Length < 2) return 0;
            Array.Reverse(bytes);
            return BitConverter.ToUInt16(bytes, 0);
        }

        private int ReadInt32BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length < 4) return 0;
            Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }
    }
}