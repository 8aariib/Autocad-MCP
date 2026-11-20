using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AutoCADChat
{
    /// <summary>A small Markdown renderer for chat answers: headings, paragraphs,
    /// bullet and numbered lists, fenced code, tables (as monospace), and inline
    /// **bold**, *italic* and `code`. Anything else is shown as plain text.</summary>
    public static class Markdown
    {
        private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
        private static readonly Regex ListItem = new(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex Inline = new(@"(\*\*[^*]+\*\*|`[^`]+`|\*[^*\s][^*]*\*|_[^_\s][^_]*_)", RegexOptions.Compiled);

        public static FlowDocument Render(string text, Theme t, double fontSize)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = fontSize,
                Foreground = t.Text,
                Background = Brushes.Transparent,
            };
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            var para = new List<string>();
            List? list = null;
            bool listOrdered = false;

            void FlushPara()
            {
                if (para.Count == 0) return;
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                AddInlines(p.Inlines, string.Join(" ", para), t);
                doc.Blocks.Add(p);
                para.Clear();
            }

            void FlushList()
            {
                if (list != null) doc.Blocks.Add(list);
                list = null;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("```"))
                {
                    FlushPara();
                    FlushList();
                    var code = new List<string>();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) code.Add(lines[i++]);
                    doc.Blocks.Add(CodeBlock(string.Join("\n", code), t, fontSize));
                    continue;
                }
                if (line.TrimStart().StartsWith("|"))
                {
                    FlushPara();
                    FlushList();
                    var rows = new List<string>();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                    {
                        if (!Regex.IsMatch(lines[i], @"^\s*\|[\s:|-]+\|\s*$")) rows.Add(lines[i].Trim());
                        i++;
                    }
                    i--;
                    doc.Blocks.Add(CodeBlock(string.Join("\n", rows), t, fontSize));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushPara();
                    FlushList();
                    continue;
                }
                var h = Heading.Match(line);
                if (h.Success)
                {
                    FlushPara();
                    FlushList();
                    var p = new Paragraph { Margin = new Thickness(0, 4, 0, 4), FontWeight = FontWeights.SemiBold, FontSize = fontSize + (h.Groups[1].Length <= 2 ? 2 : 1) };
                    AddInlines(p.Inlines, h.Groups[2].Value, t);
                    doc.Blocks.Add(p);
                    continue;
                }
                var li = ListItem.Match(line);
                if (li.Success)
                {
                    FlushPara();
                    bool ordered = char.IsDigit(li.Groups[2].Value[0]);
                    if (list == null || listOrdered != ordered)
                    {
                        FlushList();
                        list = new List
                        {
                            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                            Margin = new Thickness(0, 0, 0, 6),
                            Padding = new Thickness(18, 0, 0, 0),
                        };
                        listOrdered = ordered;
                    }
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                    if (li.Groups[1].Value.Length >= 2) p.Margin = new Thickness(14, 0, 0, 2);
                    AddInlines(p.Inlines, li.Groups[3].Value, t);
                    list.ListItems.Add(new ListItem(p));
                    continue;
                }
                FlushList();
                para.Add(line.Trim());
            }
            FlushPara();
            FlushList();
            return doc;
        }

        private static Block CodeBlock(string code, Theme t, double fontSize)
        {
            var p = new Paragraph(new Run(code))
            {
                FontFamily = Mono,
                FontSize = fontSize - 1,
                Background = t.CodeBg,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 2, 0, 8),
            };
            return p;
        }

        public static void AddInlines(InlineCollection target, string text, Theme t)
        {
            int last = 0;
            foreach (Match m in Inline.Matches(text))
            {
                if (m.Index > last) target.Add(new Run(text.Substring(last, m.Index - last)));
                var v = m.Value;
                if (v.StartsWith("**")) target.Add(new Bold(new Run(v.Substring(2, v.Length - 4))));
                else if (v.StartsWith("`"))
                    target.Add(new Run(v.Substring(1, v.Length - 2)) { FontFamily = Mono, Background = t.CodeBg });
                else target.Add(new Italic(new Run(v.Substring(1, v.Length - 2))));
                last = m.Index + m.Length;
            }
            if (last < text.Length) target.Add(new Run(text.Substring(last)));
        }
    }
}
