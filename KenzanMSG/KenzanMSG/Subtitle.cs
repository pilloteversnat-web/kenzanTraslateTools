using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KenzanMSG
{
    public class Subtitle
    {
        public long Offset { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public bool Loaded { get; set; }
        public MsgProperties Properties { get; set; } = new();

        public MsgProperties TranslationProperties
        {
            get
            {
                var newProperties = new MsgProperties();
                var translationProperties = ExtractProperties(Translation);
                newProperties.AddRange(translationProperties);

                int i = 0;
                short cleanTextLength = (short)CleanTranslation().Length;
                foreach (var property in Properties)
                {
                    if (property.GetType().Name == nameof(MsgProperty))
                    {
                        var copy = new MsgProperty(property.ToByteArray());
                        if (property.IsEndProperty || property.Position > cleanTextLength)
                        {
                            copy.Position = cleanTextLength;
                        }
                        copy.Order = i;
                        newProperties.Add(copy);
                    }
                    i++;
                }
                return newProperties;
            }
        }

        public Subtitle() { }

        public Subtitle(Subtitle s)
        {
            Offset = s.Offset;
            Text = s.Text;
            Translation = s.Translation;
            Loaded = s.Loaded;
        }

        private static string Clean(string text, bool removeLineBreaks, bool removeTags)
        {
            var lineBreakPattern = @"(\\r\\n)|(\\n)";
            var colorPattern = @"(<Color[^>]*>)";
            var tagPattern = @"(<[^>]*>)";

            var temp = text;
            if (removeLineBreaks) temp = Regex.Replace(temp, lineBreakPattern, "#");
            if (removeTags)
            {
                temp = Regex.Replace(temp, colorPattern, string.Empty);
                temp = Regex.Replace(temp, tagPattern, "#");
            }
            return temp;
        }

        public string CleanText() => Clean(Text, true, true);
        public string CleanTranslation() => Clean(Translation, true, true);

        private static MsgProperties ExtractProperties(string text)
        {
            var result = new MsgProperties();
            var cleanText = Clean(text, true, true);
            var textWithTags = Clean(text, true, false);

            var shortPauses = Regex.Matches(cleanText, @"(,\s)|(、)");
            foreach (Match pause in shortPauses)
            {
                result.Add(new PauseMsgProperty { Duration = 10, Position = (short)(pause.Index + 1), Order = -1 });
            }

            var longPauses = Regex.Matches(cleanText, @"([\?|？][\s|#])|([\!|！][\s|#])|(\.\s)|(。[\s]*)|(…[^…）？！])");
            foreach (Match pause in longPauses)
            {
                result.Add(new PauseMsgProperty { Duration = 20, Position = (short)(pause.Index + 1), Order = -1 });
            }

            var openParenthesis = Regex.Matches(cleanText, @"([\(|（])");
            foreach (Match parenthesis in openParenthesis)
            {
                result.Add(new ParenthesisStartMsgProperty { Position = (short)parenthesis.Index, Order = -1 });
            }

            var closeParenthesis = Regex.Matches(cleanText, @"([\)|）])");
            foreach (Match parenthesis in closeParenthesis)
            {
                result.Add(new ParenthesisEndMsgProperty { Position = (short)(parenthesis.Index + 1), Order = -1 });
            }

            return result;
        }
    }
}