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

            // 1. Localizamos dónde empiezan los datos de texto reales (por defecto a partir de 0x200 o buscando el primer offset mínimo)
            int dataStartOffset = 0x200;

            // Copiamos la cabecera estructural intacta
            var newFileStream = new MemoryStream();
            newFileStream.Write(originalBytes, 0, dataStartOffset);

            // Ordenamos los subtítulos por su aparición original
            var sortedSubtitles = subtitles.Where(s => s.Offset >= 0x200).OrderBy(s => s.Offset).ToList();

            // Diccionario para mapear el offset viejo con el nuevo offset recalculado
            var offsetMapping = new Dictionary<long, int>();

            // 2. Escribimos cada texto traducido de forma consecutiva y guardamos su nueva posición
            foreach (var sub in sortedSubtitles)
            {
                string textToUse = !string.IsNullOrEmpty(sub.Translation) ? sub.Translation : sub.Text;
                byte[] textBytes = FileEncoding.GetBytes(textToUse);

                int newOffset = (int)newFileStream.Position;
                offsetMapping[sub.Offset] = newOffset;

                newFileStream.Write(textBytes, 0, textBytes.Length);
                newFileStream.WriteByte(0x00); // Terminador nulo obligatorio entre cadenas
            }

            // Alineación múltiplo de 4 bytes al final del archivo (común en motores de PS3)
            while (newFileStream.Length % 4 != 0)
            {
                newFileStream.WriteByte(0x00);
            }

            byte[] rebuiltBytes = newFileStream.ToArray();

            // 3. Reconstruimos y actualizamos la tabla de punteros de la cabecera (Big-Endian)
            for (long p = 0x18; p < dataStartOffset - 4; p += 4)
            {
                int oldPointerVal = ReadInt32BigEndianAt(originalBytes, (int)p);

                // Si este puntero apuntaba a uno de nuestros strings, lo actualizamos con su nueva dirección
                if (offsetMapping.ContainsKey(oldPointerVal))
                {
                    int updatedOffset = offsetMapping[oldPointerVal];
                    byte[] pointerBytes = BitConverter.GetBytes(updatedOffset);
                    Array.Reverse(pointerBytes); // Formato Big-Endian para PS3
                    Array.Copy(pointerBytes, 0, rebuiltBytes, p, 4);
                }
                else
                {
                    // Si el puntero original apuntaba a otra zona, lo copiamos tal cual
                    Array.Copy(originalBytes, p, rebuiltBytes, p, 4);
                }
            }

            // 4. Escritura atómica limpia mediante archivo temporal (.tmp)
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

        private int ReadInt32BigEndianAt(byte[] buffer, int index)
        {
            if (index + 4 > buffer.Length) return 0;
            byte[] bytes = new byte[4];
            Array.Copy(buffer, index, bytes, 0, 4);
            Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
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