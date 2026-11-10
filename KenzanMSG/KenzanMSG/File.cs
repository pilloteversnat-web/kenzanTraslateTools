using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KenzanMSG
{
    /// <summary>
    /// Lector / reconstructor de archivos MSG de Yakuza Kenzan (PS3, big-endian).
    ///
    /// Estructura del archivo (offsets absolutos):
    ///   0x00-0x5F  Cabecera. 0x3C = puntero a la tabla de nombres/etiquetas del final,
    ///              0x40 = cantidad de punteros de esa tabla, 0x50/0x54 = tabla de grupos.
    ///   tabla de grupos (12 bytes c/u): [..][..][..][cantidad u16][puntero a tabla1 u32]
    ///   tabla1 (16 bytes c/u): [X u32][offset subtabla u32][cantidad u16][0x0664 u16][flags u32]
    ///   subtablas (12 bytes c/u): [largo texto u16][nº bloques u8][flag u8][offset texto u32][offset bloques u32]
    ///   bloques (16 bytes c/u) por mensaje: estilo, pausas y marca de fin. La posición (u16) está en los
    ///              bytes 6-7 y se mide en CARACTERES VISIBLES (sin etiquetas, "\n" cuenta 1).
    ///   textos     cadenas terminadas en 0x00 (una cadena vacía ocupa 1 byte).
    ///   final      (alineado a 4) tabla de punteros + nombres/etiquetas.
    ///
    /// El juego suma la dirección base a estos punteros EN MEMORIA, así que cada offset debe seguir
    /// apuntando donde corresponde. Por eso la reconstrucción NO puede volcar texto en 0x200:
    /// hay que regenerar bloques, textos y tabla final y actualizar todos los punteros.
    /// </summary>
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
            /// <summary>Posición del registro de 12 bytes de este mensaje (se usa como identificador).</summary>
            public long HeaderPosition { get; set; }
            /// <summary>Offset original del texto (solo informativo, para el .txt exportado).</summary>
            public long Offset { get; set; }
            public string Text { get; set; } = string.Empty;
            public string Translation { get; set; } = string.Empty;
            public bool Loaded { get; set; }
        }

        // ------------------------------------------------------------------
        //  Modelo interno
        // ------------------------------------------------------------------
        private class MsgRecord
        {
            public int Pos;                 // posición del registro de 12 bytes
            public byte Flag;
            public int TextPtr;
            public int BlocksPtr;
            public byte[] Text = Array.Empty<byte>();
            public List<byte[]> Blocks = new();

            public byte[] NewText = Array.Empty<byte>();
            public List<byte[]> NewBlocks = new();
            public int NewTextPtr;
            public int NewBlocksPtr;
        }

        private class Table1Entry
        {
            public int Pos;
            public uint X;
        }

        private class MsgDocument
        {
            public List<Table1Entry> Table1 = new();
            public List<MsgRecord> Records = new();
            public int TailPtr;
            public List<byte[]> TailStrings = new();
        }

        // ------------------------------------------------------------------
        //  Helpers big-endian
        // ------------------------------------------------------------------
        private static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

        private static uint U32(byte[] d, int o) =>
            ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

        private static void W16(byte[] d, int o, int v)
        {
            d[o] = (byte)(v >> 8);
            d[o + 1] = (byte)v;
        }

        private static void W32(byte[] d, int o, int v)
        {
            d[o] = (byte)(v >> 24);
            d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8);
            d[o + 3] = (byte)v;
        }

        private static byte[] Slice(byte[] d, int start, int len) => d.AsSpan(start, len).ToArray();

        // ------------------------------------------------------------------
        //  Parser
        // ------------------------------------------------------------------
        private static MsgDocument ParseDocument(byte[] d)
        {
            if (d.Length < 0x68)
                throw new InvalidDataException("El archivo es demasiado pequeño para ser un MSG de Kenzan.");

            int groupTable = (int)U32(d, 0x50);
            int groupCount = U16(d, 0x54);
            if (groupCount == 0 || groupTable < 0 || groupTable + 12 * groupCount > d.Length)
                throw new InvalidDataException("Tabla de grupos inválida (0x50/0x54).");

            int expectedTable1 = 0;
            for (int g = 0; g < groupCount; g++)
                expectedTable1 += U16(d, groupTable + 12 * g + 6);

            int table1Start = (int)U32(d, groupTable + 8);
            if (table1Start < 0 || table1Start + 16 > d.Length)
                throw new InvalidDataException("Puntero a tabla1 inválido.");

            var doc = new MsgDocument();

            int p = table1Start;
            while (p + 16 <= d.Length && U16(d, p + 10) == 0x0664)
            {
                doc.Table1.Add(new Table1Entry { Pos = p, X = U32(d, p) });

                int subOffset = (int)U32(d, p + 4);
                int count = U16(d, p + 8);

                for (int k = 0; k < count; k++)
                {
                    int q = subOffset + 12 * k;
                    if (q < 0 || q + 12 > d.Length)
                        throw new InvalidDataException($"Registro fuera de rango en 0x{q:X}.");

                    var rec = new MsgRecord
                    {
                        Pos = q,
                        Flag = d[q + 3],
                        TextPtr = (int)U32(d, q + 4),
                        BlocksPtr = (int)U32(d, q + 8)
                    };
                    int length = U16(d, q);
                    int blockCount = d[q + 2];

                    if (rec.TextPtr < 0 || rec.TextPtr + length >= d.Length)
                        throw new InvalidDataException($"Texto fuera de rango en el registro 0x{q:X}.");
                    if (rec.BlocksPtr < 0 || rec.BlocksPtr + 16 * blockCount > d.Length)
                        throw new InvalidDataException($"Bloques fuera de rango en el registro 0x{q:X}.");

                    rec.Text = Slice(d, rec.TextPtr, length);
                    for (int i = 0; i < blockCount; i++)
                        rec.Blocks.Add(Slice(d, rec.BlocksPtr + 16 * i, 16));

                    doc.Records.Add(rec);
                }
                p += 16;
            }

            if (doc.Table1.Count == 0 || doc.Table1.Count != expectedTable1 || doc.Records.Count == 0)
                throw new InvalidDataException("Estructura de tablas no reconocida (¿no es un MSG de Kenzan?).");

            // Tabla final (nombres / etiquetas)
            doc.TailPtr = (int)U32(d, 0x3C);
            int tailCount = U16(d, 0x40);
            if (doc.TailPtr < 0 || doc.TailPtr + 4 * tailCount > d.Length)
                throw new InvalidDataException("El puntero de 0x3C apunta fuera del archivo (archivo truncado o dañado).");

            for (int i = 0; i < tailCount; i++)
            {
                int ptr = (int)U32(d, doc.TailPtr + 4 * i);
                if (ptr < 0 || ptr >= d.Length)
                    throw new InvalidDataException("Puntero de la tabla final fuera de rango.");
                int end = Array.IndexOf(d, (byte)0, ptr);
                if (end < 0) throw new InvalidDataException("Cadena final sin terminador.");
                doc.TailStrings.Add(Slice(d, ptr, end - ptr));
            }

            return doc;
        }

        // ------------------------------------------------------------------
        //  API pública
        // ------------------------------------------------------------------

        /// <summary>
        /// Devuelve los mensajes no vacíos en el orden real del archivo.
        /// Lanza InvalidDataException si el archivo no tiene la estructura esperada.
        /// </summary>
        public List<Subtitle> GetSubtitles()
        {
            var doc = ParseDocument(System.IO.File.ReadAllBytes(Path));
            var result = new List<Subtitle>();

            foreach (var rec in doc.Records)
            {
                if (rec.Text.Length == 0) continue;
                string text = FileEncoding.GetString(rec.Text);
                result.Add(new Subtitle
                {
                    HeaderPosition = rec.Pos,
                    Offset = rec.TextPtr,
                    Text = text,
                    Translation = text,
                    Loaded = true
                });
            }
            return result;
        }

        /// <param name="tailNameReplacements">
        /// Opcional: reemplazos para los nombres de la tabla final (p. ej. "Pawn Shop Owner" -> "Prestamista").
        /// Las etiquetas internas (Talk_Yes, TlkMShop_seiza_no...) no se tocan salvo que las incluyas.
        /// </param>
        public void RebuildWithSubtitles(string outputFolder, List<Subtitle> subtitles,
                                         Dictionary<string, string>? tailNameReplacements = null)
        {
            var outputPath = System.IO.Path.Combine(outputFolder, System.IO.Path.GetFileName(Path));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

            byte[] original = System.IO.File.ReadAllBytes(Path);
            bool sameFile = string.Equals(System.IO.Path.GetFullPath(Path),
                                          System.IO.Path.GetFullPath(outputPath),
                                          StringComparison.OrdinalIgnoreCase);

            if (subtitles == null || subtitles.Count == 0)
            {
                if (!sameFile) System.IO.File.Copy(Path, outputPath, true);
                return;
            }

            var doc = ParseDocument(original);
            var byPos = subtitles.GroupBy(s => s.HeaderPosition).ToDictionary(g => g.Key, g => g.First());

            // 1) Texto nuevo + bloques regenerados para cada mensaje modificado
            foreach (var rec in doc.Records)
            {
                rec.NewText = rec.Text;
                rec.NewBlocks = rec.Blocks;

                if (byPos.TryGetValue(rec.Pos, out var sub) && !string.IsNullOrEmpty(sub.Translation))
                {
                    byte[] newBytes = FileEncoding.GetBytes(MakeEncodable(sub.Translation));
                    if (!newBytes.AsSpan().SequenceEqual(rec.Text))
                    {
                        rec.NewText = newBytes;
                        rec.NewBlocks = RemapBlocks(rec, newBytes);
                    }
                }

                if (rec.NewText.Length > ushort.MaxValue)
                    throw new InvalidDataException($"El mensaje en 0x{rec.Pos:X} supera los 65535 bytes.");
                if (rec.NewBlocks.Count > byte.MaxValue)
                    throw new InvalidDataException($"El mensaje en 0x{rec.Pos:X} tiene demasiados bloques.");
            }

            // 2) Ensamblado: [cabecera+tablas originales][bloques][textos][pad][tabla final]
            int blocksStart = doc.Records.Min(r => r.BlocksPtr);
            int recordsEnd = doc.Records.Max(r => r.Pos) + 12;
            if (blocksStart < recordsEnd || blocksStart > original.Length)
                throw new InvalidDataException("La región de bloques no está donde se esperaba.");

            var ms = new MemoryStream();
            ms.Write(original, 0, blocksStart);

            foreach (var rec in doc.Records)
            {
                rec.NewBlocksPtr = (int)ms.Position;
                foreach (var b in rec.NewBlocks) ms.Write(b, 0, 16);
            }

            int textStart = (int)ms.Position;
            foreach (var rec in doc.Records)
            {
                rec.NewTextPtr = (int)ms.Position;
                ms.Write(rec.NewText, 0, rec.NewText.Length);
                ms.WriteByte(0x00);
            }

            while (ms.Position % 4 != 0) ms.WriteByte(0x00);

            int tailPtr = (int)ms.Position;
            var tailStrings = doc.TailStrings.Select(s =>
            {
                if (tailNameReplacements != null &&
                    tailNameReplacements.TryGetValue(FileEncoding.GetString(s), out var rep))
                    return FileEncoding.GetBytes(MakeEncodable(rep));
                return s;
            }).ToList();

            int stringPos = tailPtr + 4 * tailStrings.Count;
            var ptrBytes = new byte[4];
            foreach (var s in tailStrings)
            {
                W32(ptrBytes, 0, stringPos);
                ms.Write(ptrBytes, 0, 4);
                stringPos += s.Length + 1;
            }
            foreach (var s in tailStrings)
            {
                ms.Write(s, 0, s.Length);
                ms.WriteByte(0x00);
            }

            byte[] result = ms.ToArray();

            // 3) Parcheo de punteros / longitudes
            foreach (var rec in doc.Records)
            {
                W16(result, rec.Pos, rec.NewText.Length);          // largo SIN el terminador
                result[rec.Pos + 2] = (byte)rec.NewBlocks.Count;
                result[rec.Pos + 3] = rec.Flag;
                W32(result, rec.Pos + 4, rec.NewTextPtr);
                W32(result, rec.Pos + 8, rec.NewBlocksPtr);
            }
            foreach (var t in doc.Table1)
            {
                // En los MSG ya traducidos este campo apunta al inicio del texto; en el original vale 0.
                if (t.X != 0) W32(result, t.Pos, textStart);
            }
            W32(result, 0x3C, tailPtr);

            // 4) Escritura segura (con copia de respaldo la primera vez)
            if (sameFile)
            {
                var bak = outputPath + ".bak";
                if (!System.IO.File.Exists(bak)) System.IO.File.Copy(Path, bak);
            }

            string tempPath = outputPath + ".tmp";
            if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
            System.IO.File.WriteAllBytes(tempPath, result);
            if (System.IO.File.Exists(outputPath)) System.IO.File.Delete(outputPath);
            System.IO.File.Move(tempPath, outputPath);
        }

        // ------------------------------------------------------------------
        //  Regeneración de bloques (pausas, colores, posiciones)
        // ------------------------------------------------------------------
        private static readonly Regex TagRegex = new(@"<[^>]*>", RegexOptions.Compiled);

        /// <summary>Texto tal como lo cuenta el juego: sin etiquetas y con "\r\n" = 1 carácter.</summary>
        private static string Visible(string s) => TagRegex.Replace(s, string.Empty).Replace("\r\n", "\n");

        private static bool IsPause(byte[] b) => b[0] == 2 && b[1] == 1;
        private static int BlockPos(byte[] b) => (b[6] << 8) | b[7];

        private static byte[] WithPos(byte[] b, int pos)
        {
            var c = (byte[])b.Clone();
            c[6] = (byte)(pos >> 8);
            c[7] = (byte)pos;
            return c;
        }

        private static byte[] MakePause(int pos, byte type)
        {
            var b = new byte[16];
            b[0] = 2; b[1] = 1; b[3] = type;
            b[6] = (byte)(pos >> 8); b[7] = (byte)pos;
            return b;
        }

        private static bool IsPunct(char c) => c == '.' || c == ',' || c == '?' || c == '!';

        /// <summary>
        /// Pausa tras cada grupo de puntuación (.,?!) seguido de espacio, salto de línea o fin de texto.
        /// Coma = 0x0A (corta), resto = 0x14 (larga). Siempre hay una pausa final en el último carácter.
        /// (Regla verificada contra los MSG ingleses: coincide en 102 de 104 mensajes con pausas.)
        /// </summary>
        private static List<(int pos, byte type)> GeneratePauses(string v)
        {
            var res = new List<(int pos, byte type)>();
            int i = 0;
            while (i < v.Length)
            {
                if (IsPunct(v[i]))
                {
                    int j = i;
                    while (j + 1 < v.Length && IsPunct(v[j + 1])) j++;
                    if (j + 1 == v.Length || v[j + 1] == ' ' || v[j + 1] == '\n')
                        res.Add((j + 1, v[j] == ',' ? (byte)0x0A : (byte)0x14));
                    i = j + 1;
                }
                else i++;
            }
            if (res.Count == 0 || res[res.Count - 1].pos != v.Length)
                res.Add((v.Length, 0x14));
            return res;
        }

        /// <summary>Posición visible de cada etiqueta &lt;Color:...&gt; (7 = inicio, 8 = &lt;Color:Default&gt;).</summary>
        private static List<(byte kind, int pos)> ColorTagPositions(string s)
        {
            var res = new List<(byte kind, int pos)>();
            int vis = 0, i = 0;
            while (i < s.Length)
            {
                if (s[i] == '<')
                {
                    int e = s.IndexOf('>', i);
                    if (e > i)
                    {
                        string tag = s.Substring(i, e - i + 1);
                        if (tag.StartsWith("<Color", StringComparison.Ordinal))
                            res.Add((tag == "<Color:Default>" ? (byte)8 : (byte)7, vis));
                        i = e + 1;
                        continue;
                    }
                }
                if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') { i++; continue; }
                vis++;
                i++;
            }
            return res;
        }

        private List<byte[]> RemapBlocks(MsgRecord rec, byte[] newBytes)
        {
            string oldS = FileEncoding.GetString(rec.Text);
            string newS = FileEncoding.GetString(newBytes);
            string newVis = Visible(newS);
            int oldLen = Visible(oldS).Length;
            int newLen = newVis.Length;

            var pauses = new List<(int idx, byte[] b)>();
            var others = new List<(int idx, byte[] b)>();
            for (int i = 0; i < rec.Blocks.Count; i++)
            {
                if (IsPause(rec.Blocks[i])) pauses.Add((i, rec.Blocks[i]));
                else others.Add((i, rec.Blocks[i]));
            }

            // Solo los mensajes que ya tenían pausas las vuelven a tener
            var newPauses = pauses.Count > 0 ? GeneratePauses(newVis) : new List<(int pos, byte type)>();

            // Mapa "límite original -> límite nuevo" (n-ésima pausa del original = n-ésima del nuevo)
            var oldBounds = pauses.Select(p => BlockPos(p.b)).Distinct().OrderBy(x => x).ToList();
            var newBounds = newPauses.Select(p => p.pos).Distinct().OrderBy(x => x).ToList();
            var bmap = new Dictionary<int, int>();
            if (oldBounds.Count == newBounds.Count)
                for (int i = 0; i < oldBounds.Count; i++) bmap[oldBounds[i]] = newBounds[i];

            int MapPos(int p, byte op, byte sub)
            {
                if (p == 0) return 0;
                if (p == oldLen) return newLen;
                if (p > oldLen) return p;                       // no es una posición (parámetro de botones)
                if (bmap.TryGetValue(p, out var m)) return m;
                if (op == 1 && sub == 0x0F) return p;           // bloques de opciones de menú
                if (oldLen == 0) return 0;
                return Math.Max(0, Math.Min(newLen, (int)Math.Round(p * (double)newLen / oldLen)));
            }

            // Colores: posición real de cada etiqueta en el texto nuevo
            var ctags = ColorTagPositions(newS);
            var cblk = others.Where(o => o.b[0] == 2 && (o.b[1] == 7 || o.b[1] == 8)).ToList();
            var cmap = new Dictionary<int, int>();
            if (ctags.Count == cblk.Count)
            {
                bool sameKinds = true;
                for (int k = 0; k < ctags.Count; k++)
                    if (ctags[k].kind != cblk[k].b[1]) { sameKinds = false; break; }
                if (sameKinds)
                    for (int k = 0; k < ctags.Count; k++) cmap[cblk[k].idx] = ctags[k].pos;
            }

            // Bloques no-pausa: se conserva su orden original, solo cambia la posición
            var items = new List<(int pos, bool before, byte[] b)>();
            foreach (var (i, b) in others)
            {
                int op = BlockPos(b);
                bool before = pauses.Any(pp => BlockPos(pp.b) == op && pp.idx > i);
                int np = cmap.TryGetValue(i, out var cp) ? cp : MapPos(op, b[0], b[1]);
                items.Add((np, before, WithPos(b, np)));
            }

            // Se intercalan las pausas nuevas respetando el orden relativo que tenían en el original
            var pend = newPauses.Select(p => (pos: p.pos, block: MakePause(p.pos, p.type))).ToList();
            var result = new List<byte[]>();
            int pi = 0;
            foreach (var item in items)
            {
                while (pi < pend.Count &&
                       (pend[pi].pos < item.pos || (pend[pi].pos == item.pos && !item.before)))
                {
                    result.Add(pend[pi].block);
                    pi++;
                }
                result.Add(item.b);
            }
            while (pi < pend.Count) result.Add(pend[pi++].block);

            return result;
        }

        // ------------------------------------------------------------------
        //  Caracteres que la fuente/codificación del juego no soporta
        // ------------------------------------------------------------------
        private bool IsEncodable(char c)
        {
            var s = c.ToString();
            return FileEncoding.GetString(FileEncoding.GetBytes(s)) == s;
        }

        /// <summary>
        /// cp932 no tiene á é í ó ú ñ ¿ ¡: se convertirían en '?'. Se quitan los acentos (ñ -> n) y ¿ ¡.
        /// Los caracteres japoneses y ASCII no se tocan.
        /// </summary>
        private string MakeEncodable(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch == '¿' || ch == '¡') continue;
                if (IsEncodable(ch)) { sb.Append(ch); continue; }

                foreach (char c2 in ch.ToString().Normalize(NormalizationForm.FormD))
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(c2) == UnicodeCategory.NonSpacingMark) continue;
                    sb.Append(c2);
                }
            }
            return sb.ToString();
        }
    }
}
