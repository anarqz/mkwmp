// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Mkwmp.App.Controls;

namespace Mkwmp.App.Views.Settings;

/// <summary>A block of a Markdown text: a heading (level 1-3), a paragraph, a list item (bullet or numbered), a quote, code, or a rule.</summary>
public sealed record MarkdownBlock(MarkdownKind Kind, string Text, int Level = 0, string Marker = "");

public enum MarkdownKind { Heading, Paragraph, Item, Quote, Code, Rule }

/// <summary>
/// A small, safe Markdown reader for text that comes from a server (its privacy policy): headings,
/// paragraphs, bullet and numbered lists, quotes, fenced code, rules; inline bold, italic, code and
/// links. Nothing else: HTML is shown as the text it is, an image is its alternative text, and no
/// address is ever fetched. Bounded (blocks, and the length of each).
/// </summary>
public static class MarkdownReader
{
    public const int MaxBlocks = 1500;
    public const int MaxBlockLength = 8000;

    public static IReadOnlyList<MarkdownBlock> Blocks(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        var inCode = false;

        void Flush()
        {
            if (paragraph.Length > 0)
            {
                Add(new MarkdownBlock(MarkdownKind.Paragraph, paragraph.ToString()));
                paragraph.Clear();
            }
        }

        void Add(MarkdownBlock block)
        {
            if (blocks.Count < MaxBlocks)
            {
                blocks.Add(block.Text.Length > MaxBlockLength ? block with { Text = block.Text[..MaxBlockLength] } : block);
            }
        }

        foreach (var raw in (markdown ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    Add(new MarkdownBlock(MarkdownKind.Code, code.ToString().TrimEnd('\n')));
                    code.Clear();
                }
                else
                {
                    Flush();
                }
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                code.Append(raw).Append('\n');
                continue;
            }
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                if (level <= 6 && trimmed.Length > level && trimmed[level] == ' ')
                {
                    Flush();
                    Add(new MarkdownBlock(MarkdownKind.Heading, trimmed[level..].Trim().TrimEnd('#').Trim(), Math.Min(level, 3)));
                    continue;
                }
            }
            if (trimmed.Length >= 3 && trimmed.All(c => c is '-' or '*' or '_' or ' ') && trimmed.Count(c => c != ' ') >= 3 && trimmed.Where(c => c != ' ').Distinct().Count() == 1)
            {
                Flush();
                Add(new MarkdownBlock(MarkdownKind.Rule, ""));
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                Flush();
                var text = trimmed[1..].TrimStart();
                if (blocks.Count > 0 && blocks[^1].Kind == MarkdownKind.Quote)
                {
                    blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + " " + text };
                }
                else
                {
                    Add(new MarkdownBlock(MarkdownKind.Quote, text));
                }
                continue;
            }
            if (trimmed.Length > 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
            {
                Flush();
                Add(new MarkdownBlock(MarkdownKind.Item, trimmed[2..].Trim(), Marker: "•"));
                continue;
            }
            var digits = trimmed.TakeWhile(char.IsAsciiDigit).Count();
            if (digits is > 0 and <= 4 && trimmed.Length > digits + 1 && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ')
            {
                Flush();
                Add(new MarkdownBlock(MarkdownKind.Item, trimmed[(digits + 2)..].Trim(), Marker: trimmed[..digits] + "."));
                continue;
            }
            // a line that goes on: the paragraph, or the list item / quote above it
            if (paragraph.Length == 0 && line.StartsWith(' ') && blocks.Count > 0 && blocks[^1].Kind is MarkdownKind.Item)
            {
                blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + " " + trimmed };
                continue;
            }
            paragraph.Append(paragraph.Length > 0 ? " " : "").Append(trimmed);
        }
        if (inCode && code.Length > 0)
        {
            Add(new MarkdownBlock(MarkdownKind.Code, code.ToString().TrimEnd('\n')));
        }
        Flush();
        return blocks;
    }

    /// <summary>A piece of a line: plain, bold, italic, code, or a link (its text and where it goes).</summary>
    public sealed record Span(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null);

    /// <summary>Inline marks of one block: <c>**bold**</c>, <c>*italic*</c> / <c>_italic_</c>, <c>`code`</c>, <c>[text](https://…)</c>; <c>![alt](…)</c> is its alt text.</summary>
    public static IReadOnlyList<Span> Spans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new List<Span>();
        var plain = new StringBuilder();
        var (bold, italic) = (false, false);

        void Flush()
        {
            if (plain.Length > 0)
            {
                spans.Add(new Span(plain.ToString(), bold, italic));
                plain.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_[]()#!>-.".Contains(text[i + 1], StringComparison.Ordinal))
            {
                plain.Append(text[++i]);
            }
            else if (c == '`' && text.IndexOf('`', i + 1) is var end and > 0)
            {
                Flush();
                spans.Add(new Span(text[(i + 1)..end], Code: true));
                i = end;
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                Flush();
                bold = !bold;
                i++;
            }
            else if (c is '*' or '_' && (c == '*' || Boundary(text, i)))
            {
                Flush();
                italic = !italic;
            }
            else if (c is '[' or '!' && TryLink(text, c == '!' ? i + 1 : i, out var label, out var url, out var after) && (c == '[' || (i + 1 < text.Length && text[i + 1] == '[')))
            {
                Flush();
                // an image is never fetched: its alternative text stands for it
                spans.Add(c == '!' ? new Span(label, bold, italic) : new Span(label, bold, italic, Link: SafeLink(url)));
                i = after - 1;
            }
            else
            {
                plain.Append(c);
            }
        }
        Flush();
        return spans;
    }

    /// <summary>An underscore marks italics only at the edge of a word (snake_case stays as it is).</summary>
    private static bool Boundary(string text, int at) =>
        at == 0 || at == text.Length - 1 || !char.IsLetterOrDigit(text[at - 1]) || !char.IsLetterOrDigit(text[at + 1]);

    private static bool TryLink(string text, int at, out string label, out string url, out int after)
    {
        (label, url, after) = ("", "", at);
        if (at >= text.Length || text[at] != '[')
        {
            return false;
        }
        var close = text.IndexOf(']', at + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            return false;
        }
        var end = text.IndexOf(')', close + 2);
        if (end < 0)
        {
            return false;
        }
        label = text[(at + 1)..close];
        url = text[(close + 2)..end].Trim().Split(' ')[0];
        after = end + 1;
        return true;
    }

    /// <summary>Only an https page may be opened; anything else is shown as text and goes nowhere.</summary>
    public static string? SafeLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && url!.Length <= 500 ? uri.AbsoluteUri : null;
}

/// <summary>
/// Shows a Markdown text in the launcher's own type, for a controller: the text is cut into
/// sections (at its headings), each a card that takes the focus, so the D-pad walks the page
/// section by section and the scroll follows; every link of a section is a row under it (its
/// address shown), opened only when chosen. Set <see cref="Markdown"/>; <see cref="LinkCommand"/>
/// gets the address.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", (d, _) => ((MarkdownView)d).Build()));

    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.Register(
        nameof(LinkCommand), typeof(ICommand), typeof(MarkdownView), new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Build()));

    /// <summary>At most this many blocks in one card: a long section is cut, so a card fits the screen.</summary>
    private const int BlocksPerCard = 7;

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Executed with a link's address (https only) when its row is chosen.</summary>
    public ICommand? LinkCommand
    {
        get => (ICommand?)GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    private void Build()
    {
        Children.Clear();
        var card = new List<MarkdownBlock>();
        foreach (var block in MarkdownReader.Blocks(Markdown))
        {
            if ((block.Kind == MarkdownKind.Heading && card.Count > 0) || card.Count >= BlocksPerCard)
            {
                AddCard(card);
                card = [];
            }
            card.Add(block);
        }
        AddCard(card);
    }

    private void AddCard(List<MarkdownBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }
        var panel = new StackPanel();
        var links = new List<(string Text, string Url)>();
        foreach (var block in blocks)
        {
            panel.Children.Add(Render(block, panel.Children.Count == 0, links));
        }
        var button = new Button { Content = panel, Margin = new Thickness(0, Children.Count == 0 ? 0 : 14, 0, 0) };
        button.SetResourceReference(StyleProperty, "CardButton");
        Children.Add(button);
        foreach (var (text, url) in links.DistinctBy(l => l.Url))
        {
            var row = new SettingsRow { Label = text, Caption = url, Kind = RowKind.Chevron, Command = LinkCommand, CommandParameter = url };
            row.SetResourceReference(SettingsRow.IconProperty, "Icon.Globe");
            Children.Add(row);
        }
    }

    private static FrameworkElement Render(MarkdownBlock block, bool first, List<(string Text, string Url)> links)
    {
        switch (block.Kind)
        {
            case MarkdownKind.Rule:
            {
                var rule = new Border { Height = 1.5, Margin = new Thickness(0, 16, 0, 16) };
                rule.SetResourceReference(Border.BackgroundProperty, "Brush.Rule");
                return rule;
            }
            case MarkdownKind.Code:
            {
                var text = Text(block.Text, 24, 34, "Brush.Text");
                text.Text = block.Text; // as it is: no inline marks inside code
                var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(20, 14, 20, 14), Margin = new Thickness(0, first ? 0 : 12, 0, 0), Child = text };
                box.SetResourceReference(Border.BackgroundProperty, "Brush.Background");
                return box;
            }
            case MarkdownKind.Heading:
            {
                var (size, weight) = block.Level switch { 1 => (38.0, FontWeights.Bold), 2 => (32.0, FontWeights.Bold), _ => (28.0, FontWeights.SemiBold) };
                var heading = Text("", size, size * 1.3, "Brush.Text");
                heading.FontWeight = weight;
                heading.Margin = new Thickness(0, first ? 0 : 14, 0, 0);
                Fill(heading, block.Text, links);
                return heading;
            }
            case MarkdownKind.Item:
            {
                var grid = new Grid { Margin = new Thickness(6, first ? 0 : 8, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                var marker = Text("", 27, 39, "Brush.Accent");
                marker.Text = block.Marker;
                marker.FontWeight = FontWeights.Bold;
                var item = Text("", 27, 39, "Brush.Text");
                Fill(item, block.Text, links);
                Grid.SetColumn(item, 1);
                grid.Children.Add(marker);
                grid.Children.Add(item);
                return grid;
            }
            case MarkdownKind.Quote:
            {
                var quote = Text("", 27, 39, "Brush.TextMuted");
                quote.FontStyle = FontStyles.Italic;
                Fill(quote, block.Text, links);
                var bar = new Border { BorderThickness = new Thickness(5, 0, 0, 0), Padding = new Thickness(20, 4, 0, 4), Margin = new Thickness(0, first ? 0 : 12, 0, 0), Child = quote };
                bar.SetResourceReference(Border.BorderBrushProperty, "Brush.Bar");
                return bar;
            }
            default:
            {
                var paragraph = Text("", 27, 39, "Brush.Text");
                paragraph.Margin = new Thickness(0, first ? 0 : 12, 0, 0);
                Fill(paragraph, block.Text, links);
                return paragraph;
            }
        }
    }

    private static TextBlock Text(string text, double size, double line, string brush)
    {
        var block = new TextBlock { Text = text, FontSize = size, LineHeight = line, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    private static void Fill(TextBlock block, string text, List<(string Text, string Url)> links)
    {
        foreach (var span in MarkdownReader.Spans(text))
        {
            var run = new Run(span.Text);
            if (span.Bold)
            {
                run.FontWeight = FontWeights.Bold;
            }
            if (span.Italic)
            {
                run.FontStyle = FontStyles.Italic;
            }
            if (span.Code)
            {
                run.SetResourceReference(TextElement.BackgroundProperty, "Brush.Background");
                run.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");
            }
            if (span.Link is { } url)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");
                run.TextDecorations = TextDecorations.Underline;
                links.Add((span.Text, url));
            }
            block.Inlines.Add(run);
        }
    }
}
