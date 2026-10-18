using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using AdofaiModManager.Services;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using WpfTable = System.Windows.Documents.Table;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;

namespace AdofaiModManager.Controls;

/// <summary>
/// 轻量 Markdown 显示控件：把 Markdig AST 转成 WPF FlowDocument。
/// 用于在线 Mod 的介绍 / 更新说明，支持标题、段落、粗斜体、代码、链接、
/// 列表、引用、分隔线和表格；图片会异步加载。
/// </summary>
public partial class MarkdownViewer : UserControl
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x88, 0xD8));
    private static readonly Brush CodeBackground = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80));
    private static readonly Brush QuoteBorder = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80));

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata(null, OnMarkdownChanged));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata("暂无介绍。", OnMarkdownChanged));

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string? EmptyText
    {
        get => (string?)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public MarkdownViewer()
    {
        InitializeComponent();
        LinkBrush.Freeze();
        CodeBackground.Freeze();
        QuoteBorder.Freeze();
        Render();
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
        {
            viewer.Render();
        }
    }

    private void Render()
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.5,
            Foreground = TryFindBrush("TextFillColorPrimaryBrush", Brushes.Black),
            PagePadding = new Thickness(0),
            IsColumnWidthFlexible = true,
            TextAlignment = TextAlignment.Left,
        };

        var text = Markdown;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (!string.IsNullOrWhiteSpace(EmptyText))
            {
                document.Blocks.Add(new Paragraph(new Run(EmptyText))
                {
                    Foreground = TryFindBrush("TextFillColorTertiaryBrush", Brushes.Gray),
                });
            }

            Viewer.Document = document;
            return;
        }

        try
        {
            var root = Markdig.Markdown.Parse(text, Pipeline);
            AddBlocks(root, document.Blocks);
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[markdown] 解析失败：{ex.Message}");
            document.Blocks.Add(new Paragraph(new Run(text)));
        }

        if (document.Blocks.Count == 0 && !string.IsNullOrWhiteSpace(EmptyText))
        {
            document.Blocks.Add(new Paragraph(new Run(EmptyText))
            {
                Foreground = TryFindBrush("TextFillColorTertiaryBrush", Brushes.Gray),
            });
        }

        Viewer.Document = document;
    }

    private void AddBlocks(IEnumerable<Markdig.Syntax.Block> blocks, BlockCollection target)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var paragraph = new Paragraph
                    {
                        FontWeight = FontWeights.SemiBold,
                        FontSize = heading.Level switch
                        {
                            1 => 22,
                            2 => 19,
                            3 => 17,
                            _ => 15,
                        },
                        Margin = new Thickness(0, heading.Level <= 2 ? 16 : 12, 0, 7),
                    };
                    AddInlines(heading.Inline, paragraph.Inlines);
                    target.Add(paragraph);
                    break;
                }

                case ParagraphBlock paragraphBlock:
                {
                    var paragraph = new Paragraph
                    {
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                    AddInlines(paragraphBlock.Inline, paragraph.Inlines);
                    target.Add(paragraph);
                    break;
                }

                case ListBlock listBlock:
                {
                    var list = new System.Windows.Documents.List
                    {
                        MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(20, 0, 0, 8),
                        Padding = new Thickness(0),
                    };

                    foreach (var child in listBlock)
                    {
                        if (child is not ListItemBlock item)
                        {
                            continue;
                        }

                        var listItem = new ListItem();
                        AddBlocks(item, listItem.Blocks);
                        list.ListItems.Add(listItem);
                    }

                    target.Add(list);
                    break;
                }

                case QuoteBlock quote:
                {
                    var section = new Section
                    {
                        BorderBrush = QuoteBorder,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(10, 0, 0, 0),
                        Margin = new Thickness(0, 2, 0, 8),
                        FontStyle = FontStyles.Italic,
                    };
                    AddBlocks(quote, section.Blocks);
                    target.Add(section);
                    break;
                }

                case CodeBlock codeBlock:
                {
                    var code = codeBlock.Lines.ToString().TrimEnd('\r', '\n');
                    var paragraph = new Paragraph
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12.5,
                        Background = CodeBackground,
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 2, 0, 10),
                    };
                    paragraph.Inlines.Add(new Run(string.IsNullOrEmpty(code) ? " " : code));
                    target.Add(paragraph);
                    break;
                }

                case ThematicBreakBlock:
                {
                    target.Add(new Paragraph
                    {
                        BorderBrush = QuoteBorder,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Margin = new Thickness(0, 8, 0, 12),
                        FontSize = 1,
                    });
                    break;
                }

                case MdTable table:
                {
                    target.Add(BuildTable(table));
                    break;
                }

                case ContainerBlock container:
                {
                    AddBlocks(container, target);
                    break;
                }
            }
        }
    }

    private WpfTable BuildTable(MdTable source)
    {
        var table = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 2, 0, 10),
        };

        var columnCount = source.ColumnDefinitions?.Count ?? 1;
        for (var i = 0; i < columnCount; i++)
        {
            table.Columns.Add(new TableColumn());
        }

        var rowGroup = new TableRowGroup();
        table.RowGroups.Add(rowGroup);

        foreach (var child in source)
        {
            if (child is not MdTableRow sourceRow)
            {
                continue;
            }

            var row = new WpfTableRow
            {
                Background = sourceRow.IsHeader
                    ? new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80))
                    : Brushes.Transparent,
            };

            foreach (var cellChild in sourceRow)
            {
                if (cellChild is not MdTableCell sourceCell)
                {
                    continue;
                }

                var cell = new WpfTableCell
                {
                    BorderBrush = QuoteBorder,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(7, 4, 7, 4),
                };

                if (sourceRow.IsHeader)
                {
                    cell.FontWeight = FontWeights.SemiBold;
                }

                AddBlocks(sourceCell, cell.Blocks);
                if (cell.Blocks.Count == 0)
                {
                    cell.Blocks.Add(new Paragraph());
                }

                row.Cells.Add(cell);
            }

            rowGroup.Rows.Add(row);
        }

        return table;
    }

    private void AddInlines(ContainerInline? container, InlineCollection target)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = CodeBackground,
                    });
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case EmphasisInline emphasis:
                {
                    Span span = emphasis.DelimiterChar == '~'
                        ? new Span { TextDecorations = TextDecorations.Strikethrough }
                        : emphasis.DelimiterCount >= 2
                            ? new Bold()
                            : new Italic();

                    AddInlines(emphasis, span.Inlines);
                    target.Add(span);
                    break;
                }

                case LinkInline link:
                {
                    if (link.IsImage)
                    {
                        AddImage(link, target);
                        break;
                    }

                    var hyperlink = new Hyperlink();
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp ||
                         uri.Scheme == Uri.UriSchemeHttps ||
                         uri.Scheme == Uri.UriSchemeMailto))
                    {
                        hyperlink.NavigateUri = uri;
                        hyperlink.RequestNavigate += OnRequestNavigate;
                    }

                    hyperlink.Foreground = LinkBrush;
                    AddInlines(link, hyperlink.Inlines);
                    if (hyperlink.Inlines.Count == 0)
                    {
                        hyperlink.Inlines.Add(new Run(link.Url ?? link.Label ?? "链接"));
                    }

                    target.Add(hyperlink);
                    break;
                }

                case AutolinkInline autoLink:
                {
                    var hyperlink = new Hyperlink { Foreground = LinkBrush };
                    if (Uri.TryCreate(autoLink.Url, UriKind.Absolute, out var uri))
                    {
                        hyperlink.NavigateUri = uri;
                        hyperlink.RequestNavigate += OnRequestNavigate;
                    }

                    hyperlink.Inlines.Add(new Run(autoLink.Url));
                    target.Add(hyperlink);
                    break;
                }

                case ContainerInline nested:
                    AddInlines(nested, target);
                    break;
            }
        }
    }

    private void AddImage(LinkInline link, InlineCollection target)
    {
        var url = link.Url;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            target.Add(new Run(link.Label ?? "[图片]"));
            return;
        }

        var image = new Image
        {
            MaxWidth = 760,
            MaxHeight = 480,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 4, 0, 8),
        };

        var container = new InlineUIContainer(image);
        target.Add(container);

        _ = LoadImageAsync(image, url);
    }

    private static async Task LoadImageAsync(Image image, string url)
    {
        try
        {
            var source = await ImageLoader.LoadAsync(url);
            if (source is not null)
            {
                image.Source = source;
            }
        }
        catch
        {
            // 图片失败不影响文字
        }
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    private Brush TryFindBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;
}
