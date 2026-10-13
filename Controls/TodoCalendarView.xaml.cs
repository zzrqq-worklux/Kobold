using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// The todo widget's content: month calendar + the selected day's list.
    /// A renderer over <see cref="TodoCalendar"/>, <see cref="TodoListOps"/>
    /// and <see cref="TodoRollover"/> - it paints what those functions return
    /// and routes every edit back through them, never inventing its own
    /// carry/order rules. All dates come from the widget data or DateTime.Today;
    /// nothing here touches WidgetManager at load time.
    /// </summary>
    public partial class TodoCalendarView : UserControl
    {
        /// <summary>Window chrome above the content: 40 header + 2 DIP shell border.</summary>
        private const double CHROME_HEIGHT = 42;

        // The calendar/list split and every size around it come from the pure
        // layout engine (A4.1): the renderer consumes PanelLayoutAdapter's
        // constants and AdaptTodo output, so the panel never drifts from the
        // rules. The list viewport follows the engine's row count - up to six
        // rows, where it scrolls; an empty day reserves no row (fix round 12,
        // the add row sits directly under the day header).
        private const double PARENT_FONT = 14;
        private const double CHILD_FONT = 13;

        private TodoData _data;
        private TodoWidget _host;

        private DateTime _selected;
        private int _viewYear;
        private int _viewMonth;

        // At most one inline editor is open: one item, one child, or the
        // child adder under one parent.
        private string _editingItemId;
        private string _editingChildId;
        private string _addingChildToId;

        // The screen point a text click opened the editor at (fix round 17,
        // R2; fix round 19, R1): the active session's editor places the caret
        // at that character instead of selecting all - on its first build
        // and on every rebuild, so a rebuild never SelectAll()s. It lives
        // for the whole session and is cleared when the session ends or the
        // user takes the box over (clicks/types in it), so a later rebuild
        // never yanks the caret back to the original click.
        private Point? _editorCaretAt;

        // Fix round 19 (R1): the active editor was opened without a click
        // point - only the child adder - so it keeps the select-all default.
        // A click-opened session never selects all, not even after the
        // caret point above was cleared by user interaction.
        private bool _editorSelectAllOnLoad;

        // One-shot scroll targets applied by the next BuildDayList: the row
        // (item or child id) or the child adder (parent id) to bring into view.
        private string _scrollToId;
        private string _scrollToAdderForId;

        // The add row is a pill until clicked; the revealed inline box stays
        // open while it has text and collapses again when an empty one blurs.
        private bool _addBoxRevealed;

        // The pill's derived tint brushes, rebuilt per Refresh (fix round 16,
        // R1): the mouse handlers swap the background between them.
        private Brush _addPillBaseBrush;
        private Brush _addPillHoverBrush;

        // Task A4.2: the scale boxes of the today/selected ring (+ check); the
        // incremental resize path grows them with the cell without rebuilding
        // the calendar. _builtCellHeight/_builtShowLabels describe what the
        // last BuildCalendar rendered; the splitter drag anchors on the height.
        private readonly List<Viewbox> _ringBoxes = new List<Viewbox>();
        private double _builtCellHeight;
        private bool _builtShowLabels;
        private bool _splitDragging;
        private double _splitStartY;
        private double _splitStartCell;

        // The commit an inline editor deferred on focus loss. Kept so the
        // offscreen checks can run the deferred work without pumping the app
        // dispatcher; production clears it when the callback runs.
        private Action _pendingEditorCommit;

        private CultureInfo _culture;
        private Brush _accentBrush;

        public TodoCalendarView()
        {
            InitializeComponent();
        }

        /// <summary>Height the content wants: month block + the clamped list block.</summary>
        public double DesiredHeight { get; private set; }

        /// <summary>
        /// Binds the view to a widget's data. The stored selected day (or
        /// today when none was stored) decides the opened month (§5.2).
        /// </summary>
        public void Attach(TodoData data, TodoWidget host)
        {
            _data = data ?? new TodoData();
            _host = host;
            _selected = (_data.SelectedDate ?? DateTime.Today).Date;
            _viewYear = _selected.Year;
            _viewMonth = _selected.Month;
            Refresh();
        }

        /// <summary>
        /// Repaints everything from the data: call on load, after every edit
        /// and on theme/language changes. Cheap and idempotent. Task A4.1: the
        /// layout comes first - the engine splits the panel height between the
        /// calendar and the day list, and the builders only render its output.
        /// The panel size itself never follows the content. Task A4.2: the
        /// engine also positions the add row (pinned when the space runs out).
        /// </summary>
        public void Refresh()
        {
            if (_data == null) return;

            _culture = TodoCalendar.CultureForLanguage(Localization.CurrentLanguage);
            _accentBrush = new SolidColorBrush(Utils.HexToColor(_data.Color));
            _accentBrush.Freeze();

            DateTime today = DateTime.Today;
            var metrics = ComputeMetrics(today);

            BuildMonthHeader(today);
            BuildWeekdayHeader();
            BuildCalendar(today, metrics.Cell, metrics.ShowLabels);
            BuildDayList(metrics.Parents, today);
            ApplyGeometry(metrics.WeekCount, metrics.Cell, metrics.NaturalRows, metrics.ContentH, metrics.AddBlock);
            UpdateAddRowVisuals(metrics.DayIsEmpty);

            // The divider only drags while the panel is unlocked: the band is
            // hit-test-inert when locked and the logic guards again.
            SplitterBand.IsHitTestVisible = !_data.IsLocked;
            SplitterBand.Cursor = Cursors.SizeNS;

            DesiredHeight = NaturalHeight(metrics.WeekCount, metrics.NaturalRows, metrics.DayIsEmpty);
        }

        /// <summary>
        /// The content height the engine should lay out for: the live panel
        /// height while open, the persisted panel size otherwise (both minus
        /// the chrome), or the natural fallback for a panel that has neither.
        /// </summary>
        private double CurrentContentHeight(int weekCount, int naturalRows, bool dayIsEmpty)
        {
            if (_host != null)
            {
                if (_host.IsPanelOpen)
                {
                    double live = _host.Height;
                    if (!double.IsNaN(live) && live > 0) return Math.Max(0, live - CHROME_HEIGHT);
                }
                if (_data != null && _data.PanelWidth.HasValue && _data.PanelHeight.HasValue)
                    return Math.Max(0, _host.GetPanelSize().height - CHROME_HEIGHT);
            }
            return NaturalHeight(weekCount, naturalRows, dayIsEmpty);
        }

        /// <summary>
        /// The view's content height at the migration cell size: the fixed
        /// block plus the calendar plus the (capped) list rows, and the extra
        /// revealed input row on an empty day.
        /// </summary>
        private double NaturalHeight(int weekCount, int naturalRows, bool dayIsEmpty)
        {
            int rows = Math.Min(naturalRows, PanelLayoutAdapter.LIST_MAX_ROWS);
            double h = PanelLayoutAdapter.FIXED_CONTENT_H
                     + weekCount * PanelLayoutAdapter.MIGRATION_CELL_H
                     + rows * PanelLayoutAdapter.LIST_ROW_H;
            if (_addBoxRevealed && dayIsEmpty) h += PanelLayoutAdapter.LIST_ROW_H; // the revealed input row
            return h;
        }

        /// <summary>
        /// The day's natural list rows: one per item plus its children, the
        /// open child adder counting one more.
        /// </summary>
        private int CountNaturalRows(List<TodoItem> parents)
        {
            int rows = 0;
            foreach (var item in parents)
            {
                if (item == null) continue;
                rows += 1 + (item.Children?.Count ?? 0);
                if (item.Id == _addingChildToId) rows++;
            }
            return rows;
        }

        /// <summary>
        /// The vertical block the add row occupies: the revealed input row
        /// (30), the pill (30 + its 6 half-leading) - on an empty day the
        /// revealed input and the stacked pill together.
        /// </summary>
        private double AddBlockHeight(bool dayIsEmpty)
        {
            double h = 0;
            if (_addBoxRevealed) h += PanelLayoutAdapter.LIST_ROW_H;
            if (!_addBoxRevealed || dayIsEmpty) h += PanelLayoutAdapter.FIXED_ADD_ROW;
            return h;
        }

        /// <summary>
        /// The tallest calendar cell the panel height can afford once every
        /// fixed block (chrome, margins, the add row) is off: the calendar can
        /// never crowd the add row out of the panel.
        /// </summary>
        private double MaxCalendarCell(int weekCount, double contentHeight, double addBlockHeight)
        {
            double space = contentHeight - PanelLayoutAdapter.FIXED_ROOT_MARGIN_TOP
                - PanelLayoutAdapter.FIXED_MONTH_NAV - PanelLayoutAdapter.FIXED_WEEKDAY_ROW
                - PanelLayoutAdapter.FIXED_SEPARATOR - PanelLayoutAdapter.FIXED_DAY_HEADER
                - PanelLayoutAdapter.FIXED_ROOT_MARGIN_BOTTOM - addBlockHeight;
            return Math.Max(PanelLayoutAdapter.CELL_MIN, space / Math.Max(1, weekCount));
        }

        /// <summary>
        /// The cell height actually rendered: the user's explicit split when
        /// one is stored, otherwise the automatic engine split - always inside
        /// CELL_MIN..CELL_MAX and never beyond what the panel can afford.
        /// </summary>
        private double EffectiveCellHeight(int weekCount, int naturalRows, double contentHeight, double addBlockHeight)
        {
            double cell;
            if (_data.CalendarCellHeight.HasValue)
            {
                cell = _data.CalendarCellHeight.Value;
            }
            else
            {
                double autoInput = contentHeight - Math.Max(0, addBlockHeight - PanelLayoutAdapter.FIXED_ADD_ROW);
                cell = PanelLayoutAdapter.AdaptTodo(autoInput, weekCount, naturalRows,
                    Localization.CurrentLanguage == "zh").CellHeight;
            }
            double maxCell = MaxCalendarCell(weekCount, contentHeight, addBlockHeight);
            return Math.Max(PanelLayoutAdapter.CELL_MIN,
                Math.Min(cell, Math.Min(PanelLayoutAdapter.CELL_MAX, maxCell)));
        }

        /// <summary>
        /// The day's rows, the displayed weeks and the height split the three
        /// render paths share (Refresh, the incremental resize, the splitter drag).
        /// </summary>
        private struct TodoMetrics
        {
            public List<TodoItem> Parents;
            public bool DayIsEmpty;
            public int NaturalRows;
            public int WeekCount;
            public double ContentH;
            public double AddBlock;
            public double Cell;
            public bool ShowLabels;
        }

        /// <summary>
        /// One pass over the layout inputs for the current data and panel height;
        /// every render path consumes the same numbers.
        /// </summary>
        private TodoMetrics ComputeMetrics(DateTime today)
        {
            var parents = TodoListOps.ForDay(_data.Items, _selected, today);
            bool dayIsEmpty = parents.Count == 0;
            int naturalRows = CountNaturalRows(parents);
            int weekCount = TodoCalendar.WeekCount(_viewYear, _viewMonth,
                _culture.DateTimeFormat.FirstDayOfWeek);
            double contentH = CurrentContentHeight(weekCount, naturalRows, dayIsEmpty);
            double addBlock = AddBlockHeight(dayIsEmpty);
            double cell = EffectiveCellHeight(weekCount, naturalRows, contentH, addBlock);
            bool showLabels = Localization.CurrentLanguage == "zh"
                              && cell >= PanelLayoutAdapter.LABEL_MIN_CELL;

            return new TodoMetrics
            {
                Parents = parents,
                DayIsEmpty = dayIsEmpty,
                NaturalRows = naturalRows,
                WeekCount = weekCount,
                ContentH = contentH,
                AddBlock = addBlock,
                Cell = cell,
                ShowLabels = showLabels
            };
        }

        /// <summary>
        /// Applies the fitted geometry: the day grid's height, the list
        /// viewport (capped by its natural rows; the add row pins to the
        /// bottom when the space runs out) and the ring/check scale boxes.
        /// </summary>
        private void ApplyGeometry(int weekCount, double cell, int naturalRows, double contentH, double addBlock)
        {
            DayGrid.Height = weekCount * cell;
            var fit = PanelLayoutAdapter.FitTodoList(contentH, weekCount * cell, naturalRows, addBlock);
            ItemsScroller.Height = fit.Viewport;
            foreach (var box in _ringBoxes) { box.Width = cell + 4; box.Height = cell + 4; }
        }

        /// <summary>
        /// The incremental resize path (outer panel resize and splitter drags):
        /// re-fits the geometry against the live height without rebuilding the
        /// day-list rows - only a label-threshold flip rebuilds the calendar.
        /// </summary>
        internal void ApplyResizeLayout()
        {
            if (_data == null || _host == null) return;

            DateTime today = DateTime.Today;
            var metrics = ComputeMetrics(today);

            if (metrics.ShowLabels != _builtShowLabels) BuildCalendar(today, metrics.Cell, metrics.ShowLabels);
            ApplyGeometry(metrics.WeekCount, metrics.Cell, metrics.NaturalRows, metrics.ContentH, metrics.AddBlock);
            _builtCellHeight = metrics.Cell;
        }

        #region Month calendar

        private void BuildMonthHeader(DateTime today)
        {
            MonthTitle.Text = new DateTime(_viewYear, _viewMonth, 1).ToString("Y", _culture);
            TodayText.Text = Localization.Get("Todo_TodayButton");

            bool otherMonth = _viewYear != today.Year || _viewMonth != today.Month;
            TodayButton.Visibility = otherMonth ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BuildWeekdayHeader()
        {
            WeekdayGrid.Children.Clear();
            var names = _culture.DateTimeFormat.AbbreviatedDayNames;
            DayOfWeek first = _culture.DateTimeFormat.FirstDayOfWeek;

            for (int i = 0; i < 7; i++)
            {
                int day = ((int)first + i) % 7;
                WeekdayGrid.Children.Add(new TextBlock
                {
                    Text = names[day],
                    FontSize = 10,
                    Foreground = ThemeManager.MutedTextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }

        private void BuildCalendar(DateTime today, double cell, bool showLabels)
        {
            _ringBoxes.Clear();
            DayGrid.Children.Clear();
            var cells = TodoCalendar.BuildMonth(
                _viewYear, _viewMonth, _data.Items, _selected, today,
                _culture.DateTimeFormat.FirstDayOfWeek);

            DayGrid.Height = cells.Count / 7 * cell;
            foreach (var day in cells) DayGrid.Children.Add(BuildDayCell(day, showLabels, cell));
            _builtCellHeight = cell;
            _builtShowLabels = showLabels;
        }

        private UIElement BuildDayCell(TodoDayCell cell, bool showDayLabels, double cellHeight)
        {
            // The Border stays the click target only: the today/selected mark
            // is the hand-drawn ring (plus today's check) behind the content.
            var border = new Border
            {
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1)
            };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = cell.Date.Day.ToString(_culture),
                FontSize = 10,
                FontWeight = cell.IsToday ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                // Always the theme's text colour, greyed for padding days
                // (Task A4.2: the today disc that carried the white number is
                // gone; nothing sits behind the number anymore).
                Foreground = cell.InMonth ? ThemeManager.TextBrush : ThemeManager.MutedTextBrush
            });

            // Task 2.5: the zh second line (区域假日 > 节日 > 节气 > 农历日).
            var label = BuildDayCellLabel(cell, showDayLabels);
            if (label != null) stack.Children.Add(label);

            var marks = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Height = label == null ? 5 : 4,
                Opacity = cell.InMonth ? 1.0 : 0.5
            };
            AddCountMarks(marks, cell.TopLevelCount);
            stack.Children.Add(marks);

            if (cell.IsToday || cell.IsSelected)
            {
                // A Grid centres the ring (today or selected), today's check
                // (ring's bottom-right corner) and the number stack; the
                // solid disc is gone (Task A4.2).
                var overlay = new Grid();
                var ring = BuildSelectionCircle(ReadableWidgetAccentBrush(), cellHeight);
                _ringBoxes.Add(ring);
                overlay.Children.Add(ring);
                if (cell.IsToday)
                {
                    var check = BuildTodayCheck(ReadableWidgetAccentBrush(), cellHeight);
                    _ringBoxes.Add(check);
                    overlay.Children.Add(check);
                }
                overlay.Children.Add(stack);
                border.Child = overlay;
            }
            else
            {
                border.Child = stack;
            }

            border.MouseLeftButtonDown += (s, e) => { e.Handled = true; SelectDay(cell.Date); };
            return border;
        }

        /// <summary>
        /// Task 2.5: the day cell's second line, shown when the engine's
        /// ShowDayLabels allows it (Task A4.1: zh with available data and a
        /// cell at least 32 high; en/ja keep pure numbers). The region holiday
        /// mark wins when one exists, otherwise the base layer is
        /// festival > solar term > lunar day. Colours: an off day wears the
        /// widget's readable accent, a make-up workday and every base-layer
        /// label the muted theme text (the label keeps its own colour, never
        /// OnAccent). Padding days dim to the marks' 0.5. Fits the cell via
        /// the 8pt/10px slot.
        /// </summary>
        private TextBlock BuildDayCellLabel(TodoDayCell cell, bool showDayLabels)
        {
            if (!showDayLabels) return null;

            string region = PublicHolidays.NormalizeRegion(_data.HolidayRegion);
            string holiday = PublicHolidays.GetLabel(region, cell.Date);
            string text;
            Brush foreground;
            if (holiday != null)
            {
                text = holiday;
                foreground = holiday == PublicHolidays.WorkdayMark
                    ? ThemeManager.MutedTextBrush
                    : ReadableWidgetAccentBrush();
            }
            else
            {
                text = LunarCalendar.FestivalName(cell.Date)
                    ?? LunarCalendar.SolarTermName(cell.Date)
                    ?? LunarCalendar.DayName(cell.Date);
                foreground = ThemeManager.MutedTextBrush;
            }
            if (text == null) return null;

            return new TextBlock
            {
                Text = text,
                FontSize = 8,
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = foreground,
                Opacity = cell.InMonth ? 1.0 : 0.5
            };
        }

        /// <summary>
        /// Fix round 14 (R2): the human's hand-drawn selection circle, in its
        /// 100x100 coordinate space, scaled into a box centred in the cell and
        /// rotated 183 degrees about its centre. The box is the cell height
        /// + 4 (Task A4.2: it scales with the cell; it overflows the cell
        /// ~2px per side - nothing on the path clips). Every stroke and fill
        /// is the readable widget accent; non-hit-testable so the whole cell
        /// stays one click target.
        /// </summary>
        private static Viewbox BuildSelectionCircle(Brush stroke, double cellHeight)
        {
            var canvas = new Canvas
            {
                Width = 100,
                Height = 100,
                RenderTransform = new RotateTransform(183, 50, 50)
            };

            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M 45 16 L 47 13 L 49 15 L 46 18 Z"),
                Fill = stroke,
                Opacity = 0.9
            });
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M 47 14 C 33 15, 20 22, 15 36 C 10 50, 12 66, 22 78 C 32 90, 52 94, 66 90 C 80 86, 91 74, 91 58 C 91 42, 80 28, 63 20 C 55 16, 45 16, 38 20"),
                Stroke = stroke,
                StrokeThickness = 3.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            });
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M 21 73 C 30 86, 50 91, 65 86 C 78 81, 88 68, 88 52 C 88 36, 76 22, 58 16"),
                Stroke = stroke,
                StrokeThickness = 4.2,
                Opacity = 0.85,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            });
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M 47 14 C 53 11, 60 13, 62 16 C 58 15, 52 15, 49 18"),
                Stroke = stroke,
                StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            AddInkDot(canvas, 12, 45, 1.2, 0.7, stroke);
            AddInkDot(canvas, 19, 83, 1.8, 0.6, stroke);
            AddInkDot(canvas, 87, 25, 0.9, 0.8, stroke);
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M 88 62 Q 91 64 92 60"),
                Stroke = stroke,
                StrokeThickness = 1.5,
                Opacity = 0.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            return new Viewbox
            {
                Width = cellHeight + 4,
                Height = cellHeight + 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Child = canvas
            };
        }

        /// <summary>The hand-drawn check tucked into the ring's bottom-right corner.</summary>
        private static Viewbox BuildTodayCheck(Brush stroke, double cellHeight)
        {
            var canvas = new Canvas { Width = 100, Height = 100, Tag = "TodayCheck", IsHitTestVisible = false };
            canvas.Children.Add(new Path { Data = Geometry.Parse("M 72 73 C 75 78, 78 82, 81 86"),
                Stroke = stroke, StrokeThickness = 3.6, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
            canvas.Children.Add(new Path { Data = Geometry.Parse("M 81 86 C 86 76, 91 66, 98 55"),
                Stroke = stroke, StrokeThickness = 3.8, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
            canvas.Children.Add(new Path { Data = Geometry.Parse("M 90 66 L 96 57"),
                Stroke = stroke, StrokeThickness = 2.0, Opacity = 0.55, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round });
            var dot = new Ellipse { Width = 2, Height = 2, Fill = stroke, Opacity = 0.6 };
            Canvas.SetLeft(dot, 70.6); Canvas.SetTop(dot, 70.6);
            canvas.Children.Add(dot);
            return new Viewbox { Width = cellHeight + 4, Height = cellHeight + 4,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false, Child = canvas };
        }

        private static void AddInkDot(Canvas canvas, double x, double y, double radius, double opacity, Brush fill)
        {
            var dot = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = fill,
                Opacity = opacity
            };
            Canvas.SetLeft(dot, x - radius);
            Canvas.SetTop(dot, y - radius);
            canvas.Children.Add(dot);
        }

        /// <summary>Up to three dots, then a "+" when the day has more entries.</summary>
        private void AddCountMarks(Panel marks, int count)
        {
            Brush brush = _accentBrush;
            int dots = Math.Min(count, 3);
            for (int i = 0; i < dots; i++)
            {
                marks.Children.Add(new Ellipse
                {
                    Width = 3,
                    Height = 3,
                    Margin = new Thickness(1, 0, 1, 0),
                    Fill = brush
                });
            }
            if (count > 3)
            {
                marks.Children.Add(new TextBlock
                {
                    Text = "+",
                    FontSize = 8,
                    Margin = new Thickness(1, 0, 0, 0),
                    Foreground = brush,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }

        private void PrevMonth_Click(object sender, MouseButtonEventArgs e)
        {
            ShiftView(-1);
            e.Handled = true;
        }

        private void NextMonth_Click(object sender, MouseButtonEventArgs e)
        {
            ShiftView(1);
            e.Handled = true;
        }

        private void TodayButton_Click(object sender, MouseButtonEventArgs e)
        {
            SelectDay(DateTime.Today);
            e.Handled = true;
        }

        private void ShiftView(int delta)
        {
            var shifted = TodoCalendar.ShiftMonth(_viewYear, _viewMonth, delta);
            _viewYear = shifted.year;
            _viewMonth = shifted.month;
            Refresh();
        }

        /// <summary>
        /// Selects a day; a cell outside the viewed month brings its month
        /// along (clicking a grey day first changes the month, then selects).
        /// The selection is persisted with the debounced save only.
        /// </summary>
        private void SelectDay(DateTime day)
        {
            _selected = day.Date;
            _viewYear = _selected.Year;
            _viewMonth = _selected.Month;

            _data.SelectedDate = _selected;
            _host?.PersistSelection();

            Refresh();
        }

        #endregion

        #region Day list

        private void BuildDayList(List<TodoItem> parents, DateTime today)
        {
            ItemsPanel.Children.Clear();
            DayHeaderText.Text = _selected.ToString("M", _culture) + " · " +
                                 Localization.Format("Todo_ItemCountFormat", parents.Count);

            foreach (var item in parents)
            {
                if (item == null) continue;
                var parentRow = BuildParentRow(item, today);
                ItemsPanel.Children.Add(parentRow);
                if (item.Id == _scrollToId) RequestScroll(parentRow);

                if (item.Children != null)
                {
                    foreach (var child in item.Children)
                    {
                        if (child == null) continue;
                        var childRow = BuildChildRow(item, child);
                        ItemsPanel.Children.Add(childRow);
                        if (child.Id == _scrollToId) RequestScroll(childRow);
                    }
                }

                if (_addingChildToId == item.Id)
                {
                    var adderRow = BuildChildAdder(item);
                    ItemsPanel.Children.Add(adderRow);
                    if (item.Id == _scrollToAdderForId) RequestScroll(adderRow);
                }
            }

            // The targets are one-shot: a later repaint (theme, language) must
            // not re-scroll the list somewhere the user did not ask for.
            _scrollToId = null;
            _scrollToAdderForId = null;
        }

        /// <summary>
        /// Scrolls a freshly built row into the list viewport once it is laid
        /// out - an added row or an opened editor must never land below the
        /// fold. Off-screen views never load, so this stays a no-op there.
        /// </summary>
        private static void RequestScroll(FrameworkElement row)
        {
            row.Loaded += (s, e) => row.BringIntoView();
        }

        private Border BuildParentRow(TodoItem item, DateTime today)
        {
            var shell = NewRowShell(new Thickness(0));
            var row = new Grid();
            shell.Child = row;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(BuildCheckBox(item.Done, () => ToggleItem(item)));

            UIElement text;
            if (_editingItemId == item.Id)
            {
                // Smoke fix C: Enter commits and hands the caret to the
                // item's child adder; the blur path stays a plain commit.
                text = BuildEditor(item.Text, PARENT_FONT,
                    value => { CommitItemEdit(item, value, true); return true; },
                    () => { _editingItemId = null; _editorCaretAt = null; Refresh(); },
                    value => { CommitItemEdit(item, value); return true; });
            }
            else
            {
                var block = BuildItemText(item.Text, item.Done, PARENT_FONT);
                block.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    StartItemEdit(item.Id, block.PointToScreen(e.GetPosition(block)));
                };
                text = block;
            }
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            if (TodoRollover.IsCarried(item, today))
            {
                var badge = new TextBlock
                {
                    Text = "↷ " + item.Date.Value.ToString("M/d", _culture),
                    FontSize = 11,
                    Foreground = _accentBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = Localization.Get("Todo_CarriedBadge")
                };
                Grid.SetColumn(badge, 2);
                row.Children.Add(badge);
            }

            AttachRowMenu(shell, BuildParentMenu(item));
            return shell;
        }

        private Border BuildChildRow(TodoItem parent, SubTodoItem child)
        {
            var shell = NewRowShell(new Thickness(20, 0, 0, 0));
            var row = new Grid();
            shell.Child = row;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(BuildCheckBox(child.Done, () =>
            {
                TodoRollover.ToggleChild(child, !child.Done);
                AfterItemEdit();
            }));

            UIElement text;
            if (_editingChildId == child.Id)
            {
                text = BuildEditor(child.Text, CHILD_FONT,
                    value => { CommitChildEdit(parent, child, value); return true; },
                    () => { _editingChildId = null; _editorCaretAt = null; Refresh(); });
            }
            else
            {
                var block = BuildItemText(child.Text, child.Done, CHILD_FONT);
                block.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    StartChildEdit(child.Id, block.PointToScreen(e.GetPosition(block)));
                };
                text = block;
            }
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            AttachRowMenu(shell, BuildChildMenu(parent, child));
            return shell;
        }

        /// <summary>Add-child + delete for a parent row; built and tinted like the widget menus.</summary>
        private ContextMenu BuildParentMenu(TodoItem item)
        {
            return new MenuBuilder(_data.Color)
                .AddItem("Todo_AddChild", () => StartChildAdd(item.Id))
                .AddItem("Menu_DeleteItem", () => RemoveItem(item))
                .Build();
        }

        /// <summary>Delete only for a child row.</summary>
        private ContextMenu BuildChildMenu(TodoItem parent, SubTodoItem child)
        {
            return new MenuBuilder(_data.Color)
                .AddItem("Menu_DeleteItem", () => RemoveChild(parent, child))
                .Build();
        }

        /// <summary>
        /// Right-click opens the row menu. Any open editor is settled first
        /// (synchronously): its deferred blur commit would otherwise rebuild
        /// the rows right after the menu opened, under the user's click.
        /// </summary>
        private void AttachRowMenu(FrameworkElement row, ContextMenu menu)
        {
            row.ContextMenu = menu;
            row.PreviewMouseRightButtonDown += (s, e) => CommitActiveEditorNow();
        }

        /// <summary>
        /// The indented inline adder under a parent ("＋", Enter-continuous).
        /// It stays on the panel background: the caret is the only editing cue.
        /// </summary>
        private Border BuildChildAdder(TodoItem parent)
        {
            var shell = NewRowShell(new Thickness(20, 0, 0, 0));
            var row = new Grid();
            shell.Child = row;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var editor = BuildEditor(string.Empty, CHILD_FONT,
                value =>
                {
                    string text = (value ?? string.Empty).Trim();
                    if (text.Length > 0)
                    {
                        AddChildFromAdder(parent, text);
                        return true; // the rebuilt adder stays open for the next child
                    }
                    // Smoke fix C: an empty Enter moves to the next day item's
                    // adder (display order); on the last parent it jumps to the
                    // bottom add-new input instead of closing.
                    var parents = TodoListOps.ForDay(_data.Items, _selected, DateTime.Today);
                    int index = parents.FindIndex(p => p != null && p.Id == parent.Id);
                    TodoItem next = index >= 0 && index + 1 < parents.Count ? parents[index + 1] : null;
                    if (next != null)
                    {
                        StartChildAdd(next.Id); // sets the state and refreshes
                        return true;
                    }
                    // Final spec rule 4: reveal the bottom add input in the
                    // same repaint (the RevealAddBox tail inline, so the
                    // rebuild happens once), then scroll and focus it.
                    _addingChildToId = null;
                    _editorCaretAt = null;
                    _addBoxRevealed = true;
                    Refresh();
                    ScrollListToBottomIfNeeded();
                    AddBox.Focus();
                    return true;
                },
                () =>
                {
                    _addingChildToId = null;
                    _editorCaretAt = null;
                    Refresh();
                },
                value =>
                {
                    if (_addingChildToId != parent.Id) return true; // replaced by another adder
                    string text = (value ?? string.Empty).Trim();
                    _addingChildToId = null;
                    _editorCaretAt = null;
                    if (text.Length > 0)
                    {
                        var added = TodoListOps.AddChild(parent, text);
                        _scrollToId = added.Id; // show the committed child
                        AfterItemEdit();
                    }
                    else
                    {
                        Refresh(); // empty blur just closes the adder
                    }
                    return true;
                });
            Grid.SetColumn(editor, 1); // the child text column, for alignment
            row.Children.Add(editor);
            return shell;
        }

        /// <summary>Enter-adds a child and keeps the adder open for continuous entry.</summary>
        private void AddChildFromAdder(TodoItem parent, string text)
        {
            TodoListOps.AddChild(parent, text);
            _scrollToAdderForId = parent.Id; // keep the adder visible for the next child
            AfterItemEdit();
        }

        /// <summary>
        /// A list row shell: rounded, transparent, and the anchor for the row
        /// menu. No background band - the caret/selection is the editing cue.
        /// </summary>
        private static Border NewRowShell(Thickness margin)
        {
            return new Border
            {
                Height = PanelLayoutAdapter.LIST_ROW_H,
                Margin = margin,
                CornerRadius = new CornerRadius(UiTokens.RadiusControl),
                Background = Brushes.Transparent
            };
        }

        private static TextBlock BuildItemText(string text, bool done, double fontSize)
        {
            return new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = fontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = Cursors.IBeam,
                Foreground = done ? ThemeManager.MutedTextBrush : ThemeManager.TextBrush,
                TextDecorations = done ? TextDecorations.Strikethrough : null
            };
        }

        private Border BuildCheckBox(bool done, Action toggle)
        {
            var box = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(UiTokens.RadiusSmall),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Background = done ? _accentBrush : Brushes.Transparent,
                BorderBrush = done ? _accentBrush : ThemeManager.ControlBorderHoverBrush,
                BorderThickness = new Thickness(1.5)
            };
            if (done)
            {
                box.Child = new TextBlock
                {
                    Text = "✓",
                    FontSize = 11,
                    Foreground = ThemeManager.OnAccentBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            box.MouseLeftButtonDown += (s, e) => { e.Handled = true; toggle(); };
            return box;
        }

        /// <summary>
        /// Single inline editor: Enter commits (when the caller consumes the
        /// box), Esc cancels, and losing keyboard focus commits through the
        /// blur path too. The blur commit is deferred with Dispatcher.BeginInvoke
        /// so the click that blurred the box reaches its target (checkbox, ×,
        /// another row) before the list rebuilds under it. The settled flag
        /// makes the blur that follows Enter, and the one after Esc, no-ops.
        /// The box itself is invisible (no border, no background, no padding):
        /// the caret is the only editing cue and the text starts exactly where
        /// the row's text starts.
        /// </summary>
        private TextBox BuildEditor(string initial, double fontSize,
            Func<string, bool> commit, Action cancel, Func<string, bool> commitOnBlur = null)
        {
            if (commitOnBlur == null) commitOnBlur = commit;

            var box = new TextBox
            {
                Text = initial ?? string.Empty,
                FontSize = fontSize,
                Foreground = ThemeManager.TextBrush,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Height = PanelLayoutAdapter.LIST_ROW_H - 4,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            bool settled = false;

            box.Loaded += (s, e) => ApplyEditorCaret(box);
            // Fix round 19 (R1): once the user takes the box over (clicks in
            // it, types or moves the caret), a rebuild must not re-anchor the
            // caret to the original click point anymore.
            box.PreviewMouseLeftButtonDown += (s, e) => _editorCaretAt = null;
            box.TextChanged += (s, e) => _editorCaretAt = null;
            box.KeyDown += (s, e) =>
            {
                _editorCaretAt = null;
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    if (!settled && commit(box.Text)) settled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    if (settled) return;
                    settled = true;
                    cancel();
                }
            };
            box.LostKeyboardFocus += (s, e) =>
            {
                if (settled) return;
                // Fix round 19 (R2): a rebuild replaces this box with a fresh
                // one and the focus hand-off looks like a blur; the destroyed
                // box must not schedule a commit for the session the rebuild
                // kept alive (that stale commit would close it). A genuine
                // blur of a live box (still inside the view) keeps the
                // deferred-commit behavior.
                if (!IsInside(box, this)) return;
                string text = box.Text;
                Action pending = null;
                pending = () =>
                {
                    if (ReferenceEquals(_pendingEditorCommit, pending)) _pendingEditorCommit = null;
                    if (settled) return;
                    if (commitOnBlur(text)) settled = true;
                };
                _pendingEditorCommit = pending;
                box.Dispatcher.BeginInvoke(pending, DispatcherPriority.Background);
            };
            return box;
        }

        /// <summary>
        /// Focuses a freshly (re)built editor and sets its selection (fix
        /// round 17, R2; fix round 19, R1): a session opened by a text click
        /// keeps its click point for its whole life, so every rebuild of its
        /// editor re-places the caret at the clicked character instead of
        /// selecting all. The child adder - the only editor opened without a
        /// click point - keeps the select-all default. A click-opened session
        /// whose point the user cleared by interacting with the box gets no
        /// selection change at all, never a select-all.
        /// </summary>
        private void ApplyEditorCaret(TextBox box)
        {
            box.Focus();
            if (_editorSelectAllOnLoad)
            {
                box.SelectAll();
                return;
            }
            if (!_editorCaretAt.HasValue) return; // the user already took the caret over

            Point screen = _editorCaretAt.Value;

            Point local = box.PointFromScreen(screen);
            int index = box.GetCharacterIndexFromPoint(local, true);
            if (index < 0)
            {
                index = box.Text.Length;
            }
            else
            {
                // GetCharacterIndexFromPoint snaps to the nearest character and
                // never reports the trailing insertion edge, so a click past a
                // character's right edge belongs after it (past the last
                // character = the end of the text).
                Rect bounds = box.GetRectFromCharacterIndex(index, true);
                if (local.X >= bounds.Right && index < box.Text.Length) index++;
            }
            box.CaretIndex = index;
            box.SelectionLength = 0;
        }

        #endregion

        #region Edit state and callbacks

        /// <summary>Opens the item editor, closing any other editor; the click's screen point places the caret.</summary>
        private void StartItemEdit(string itemId, Point screenPoint)
        {
            _editorCaretAt = screenPoint;
            _editorSelectAllOnLoad = false;
            _editingItemId = itemId;
            _editingChildId = null;
            _addingChildToId = null;
            _scrollToId = itemId; // the editor must not open below the fold
            Refresh();
        }

        /// <summary>Opens the child editor, closing any other editor; the click's screen point places the caret.</summary>
        private void StartChildEdit(string childId, Point screenPoint)
        {
            _editorCaretAt = screenPoint;
            _editorSelectAllOnLoad = false;
            _editingItemId = null;
            _editingChildId = childId;
            _addingChildToId = null;
            _scrollToId = childId;
            Refresh();
        }

        /// <summary>Opens the child adder under one parent, closing any other editor.</summary>
        private void StartChildAdd(string itemId)
        {
            _editorCaretAt = null; // the adder keeps the select-all default
            _editorSelectAllOnLoad = true;
            _editingItemId = null;
            _editingChildId = null;
            _addingChildToId = itemId;
            _scrollToAdderForId = itemId;
            Refresh();
        }

        private void ToggleItem(TodoItem item)
        {
            TodoRollover.Toggle(item, !item.Done, DateTime.Today);
            AfterItemEdit();
        }

        private void RemoveItem(TodoItem item)
        {
            if (_editingItemId == item.Id)
            {
                _editingItemId = null;
                _editorCaretAt = null; // the session ended with the removal
            }
            if (_addingChildToId == item.Id)
            {
                _addingChildToId = null;
                _editorCaretAt = null;
            }
            TodoListOps.RemoveItem(_data, item.Id);
            AfterItemEdit();
        }

        private void RemoveChild(TodoItem parent, SubTodoItem child)
        {
            if (_editingChildId == child.Id)
            {
                _editingChildId = null;
                _editorCaretAt = null;
            }
            TodoListOps.RemoveChild(parent, child.Id);
            AfterItemEdit();
        }

        /// <summary>
        /// Settles an open editor synchronously: the right-click that opens a
        /// row menu must not leave the deferred blur commit to rebuild the rows
        /// right after the menu opened. The queued dispatcher copy of the same
        /// commit then no-ops (settled).
        /// </summary>
        private void CommitActiveEditorNow()
        {
            if (_pendingEditorCommit == null && _editingItemId == null &&
                _editingChildId == null && _addingChildToId == null)
            {
                return;
            }

            Keyboard.ClearFocus(); // a still-focused editor schedules its blur commit
            var pending = _pendingEditorCommit;
            if (pending != null) pending();
        }

        private void CommitItemEdit(TodoItem item, string value)
        {
            CommitItemEdit(item, value, false);
        }

        /// <summary>
        /// The parent editor's commit. openAdder is the Enter path (smoke fix
        /// C): a non-empty commit opens the item's child adder in the same
        /// state change - one repaint - while a blur commit passes false and
        /// stays plain. Empty text deletes the item and opens nothing either
        /// way.
        /// </summary>
        private void CommitItemEdit(TodoItem item, string value, bool openAdder)
        {
            // A deferred commit may arrive after the user opened another row's
            // editor; that newer editor must survive the rebuild.
            if (_editingItemId == item.Id)
            {
                _editingItemId = null;
                _editorCaretAt = null; // the session ended with this commit
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                TodoListOps.RemoveItem(_data, item.Id);
            }
            else
            {
                item.Text = value.Trim();
                if (openAdder)
                {
                    _addingChildToId = item.Id;
                    _scrollToAdderForId = item.Id;
                    _editorCaretAt = null;
                    _editorSelectAllOnLoad = true;
                }
            }
            AfterItemEdit();
        }

        private void CommitChildEdit(TodoItem parent, SubTodoItem child, string value)
        {
            if (_editingChildId == child.Id)
            {
                _editingChildId = null;
                _editorCaretAt = null;
            }
            if (string.IsNullOrWhiteSpace(value)) TodoListOps.RemoveChild(parent, child.Id);
            else child.Text = value.Trim();
            AfterItemEdit();
        }

        /// <summary>
        /// Every data edit ends here: repaint, then the usual save + island
        /// refresh through the base's change event. The panel size never
        /// follows the content (Task A4.1).
        /// </summary>
        private void AfterItemEdit()
        {
            Refresh();
            _host?.NotifyDataChanged();
        }

        #endregion

        #region Inline add row

        private void AddBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            string text = AddBox.Text.Trim();
            if (text.Length == 0) return;

            var added = TodoListOps.AddItem(_data, text, _selected);
            AddBox.Clear();
            // Final spec rule 5: the caret enters the new item's child adder
            // (one repaint through AfterItemEdit); the scroll target is the
            // new adder, not the row, and the add box keeps no focus.
            _editorCaretAt = null;
            _editorSelectAllOnLoad = true;
            _editingItemId = null;
            _editingChildId = null;
            _addingChildToId = added.Id;
            _scrollToAdderForId = added.Id;
            AfterItemEdit();
        }

        /// <summary>The pill's click reveals and focuses the inline input.</summary>
        private void AddPill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            RevealAddBox();
            e.Handled = true;
        }

        private void AddPill_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_addPillHoverBrush != null) AddPill.Background = _addPillHoverBrush;
        }

        private void AddPill_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_addPillBaseBrush != null) AddPill.Background = _addPillBaseBrush;
        }

        /// <summary>The revealed row is one hit target too.</summary>
        private void AddRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            RevealAddBox();
            e.Handled = true;
        }

        private void RevealAddBox()
        {
            if (!_addBoxRevealed)
            {
                _addBoxRevealed = true;
                Refresh(); // rebuild the add row (and, on an empty day, the list height)
            }
            ScrollListToBottomIfNeeded();
            AddBox.Focus();
        }

        /// <summary>
        /// Task A4.2: a click on the add row brings the list's bottom (the
        /// newest rows) into view when the viewport scrolls; an empty list has
        /// no extent and stays put.
        /// </summary>
        private void ScrollListToBottomIfNeeded()
        {
            if (ItemsScroller.ScrollableHeight <= 0) return;
            ItemsScroller.UpdateLayout();   // let the rebuilt extent settle, then scroll
            ItemsScroller.ScrollToEnd();
        }

        /// <summary>An emptied box that loses focus returns to the pill.</summary>
        private void AddBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(AddBox.Text)) return;
            _addBoxRevealed = false;
            Refresh(); // rebuild the add row (and, on an empty day, the list height)
        }

        private void UpdateAddRowVisuals(bool selectedDayIsEmpty)
        {
            if (AddPill == null) return; // during InitializeComponent

            // Rebuilt on every repaint (not once in the constructor): the
            // checkbox captures frozen ThemeManager brushes at build time, so
            // it must follow a live theme switch exactly like the list rows,
            // whose checkboxes are rebuilt by the same Refresh pass. Visual
            // only - a click just keeps the caret in the input.
            AddCheckHost.Content = BuildCheckBox(false, () => AddBox.Focus());

            // Fix round 14: the pill's border and its ＋ both followed the
            // widget's own readable colour. Fix round 16 (R1): the hollow
            // capsule keeps its shape but the background and border become
            // Material-style derived tints of the RAW widget colour (≈12% /
            // ≈30% alpha, ≈22% on hover), while the ＋ alone keeps the
            // readable widget accent. Rebuilt here so theme switches and
            // widget-colour changes both reach them.
            Color pillColor = Utils.HexToColor(_data.Color);
            _addPillBaseBrush = FrozenTint(pillColor, 0x1F);
            _addPillHoverBrush = FrozenTint(pillColor, 0x38);
            AddPill.Background = _addPillBaseBrush;
            AddPill.BorderBrush = FrozenTint(pillColor, 0x4D);
            AddPillGlyph.Foreground = ReadableWidgetAccentBrush();

            // The pill yields to the input only on a non-empty day; on an empty
            // day both stay visible and the pill stacks below the input row.
            AddPill.Visibility = !_addBoxRevealed || selectedDayIsEmpty
                ? Visibility.Visible
                : Visibility.Collapsed;
            AddInputHost.Visibility = _addBoxRevealed ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Fix round 16 (R1): a Material-style derived tint of the widget
        /// colour - same RGB, only the alpha changes.
        /// </summary>
        private static Brush FrozenTint(Color color, byte alpha)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// The widget's own colour for the pill's ＋ and border and for the
        /// calendar's selection circle. Against the theme's panel backdrop
        /// (near-white in light, near-dark in dark) a very light or very dark
        /// colour would vanish, so those extremes are darkened/lightened for
        /// readability; a normal colour reuses the view's frozen
        /// widget-colour brush.
        /// </summary>
        private Brush ReadableWidgetAccentBrush()
        {
            Color color = Utils.HexToColor(_data.Color);
            double brightness = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;

            if (!ThemeManager.IsDarkTheme && brightness > 0.8)
            {
                var fallback = new SolidColorBrush(Utils.DarkenColor(color, 0.6));
                fallback.Freeze();
                return fallback;
            }
            if (ThemeManager.IsDarkTheme && brightness < 0.3)
            {
                var fallback = new SolidColorBrush(Utils.LightenColor(color, 0.5));
                fallback.Freeze();
                return fallback;
            }
            return _accentBrush;
        }

        #endregion

        #region Splitter drag

        /// <summary>
        /// Starts a divider drag; refused on a locked panel or without data.
        /// The anchor is the effective cell height the calendar last used.
        /// </summary>
        private bool SplitterDragStarted(double localY)
        {
            if (_data == null || _data.IsLocked) return false;
            _splitDragging = true;
            _splitStartY = localY;
            _splitStartCell = _builtCellHeight;   // drag anchor: the rendered cell height
            return true;
        }

        /// <summary>
        /// One drag step: the vertical delta converts to cell height over the
        /// week count, clamps to 22..36 (never crowding the add row out) and
        /// is stored per widget; the incremental path repaints the geometry.
        /// </summary>
        private void SplitterDragMoved(double localY)
        {
            if (!_splitDragging || _data == null) return;

            var metrics = ComputeMetrics(DateTime.Today);
            double maxCell = Math.Min(PanelLayoutAdapter.CELL_MAX,
                MaxCalendarCell(metrics.WeekCount, metrics.ContentH, metrics.AddBlock));
            double cell = _splitStartCell + (localY - _splitStartY) / Math.Max(1, metrics.WeekCount);
            _data.CalendarCellHeight = Math.Max(PanelLayoutAdapter.CELL_MIN, Math.Min(cell, maxCell));
            ApplyResizeLayout();
        }

        /// <summary>The gesture end persists the explicit split through the usual save path.</summary>
        private void SplitterDragEnded()
        {
            if (!_splitDragging) return;
            _splitDragging = false;
            _host?.NotifyDataChanged();
        }

        private void SplitterBand_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!SplitterDragStarted(e.GetPosition(this).Y)) return;
            SplitterBand.CaptureMouse();
            e.Handled = true;
        }

        private void SplitterBand_MouseMove(object sender, MouseEventArgs e)
        {
            if (_splitDragging) SplitterDragMoved(e.GetPosition(this).Y);
        }

        private void SplitterBand_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_splitDragging) return;
            // End the drag (and persist) before releasing the capture:
            // ReleaseMouseCapture raises LostMouseCapture synchronously, whose
            // handler clears _splitDragging - ending afterwards would skip the
            // persistence. The later duplicate clear is harmless.
            SplitterDragEnded();
            SplitterBand.ReleaseMouseCapture();
            e.Handled = true;
        }

        /// <summary>
        /// A stolen capture (Alt+Tab, a system dialog) ends the drag without
        /// persisting: _data keeps whatever the last move wrote, and the next
        /// press starts a fresh drag from the current cell height. A normal
        /// release re-enters this after the drag has already ended (harmless).
        /// </summary>
        private void SplitterBand_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _splitDragging = false;
        }

        #endregion

        #region Outside-click blur

        /// <summary>
        /// Called by the host window before any click: a click outside the
        /// focused editor is turned into a focus loss. Clicks on non-focusable
        /// controls (checkbox, ×, day cells, another row) do not move keyboard
        /// focus on their own, which would leave the editor open.
        /// </summary>
        public void HandleMouseDownOutsideEditor(object source)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null || !IsInside(focused, this)) return;
            if (IsInside(source as DependencyObject, focused)) return;
            // The add row's visual checkbox (inside the input host) is part of
            // the input: a click there must not collapse the revealed box.
            if (ReferenceEquals(focused, AddBox) && IsInside(source as DependencyObject, AddInputHost)) return;
            Keyboard.ClearFocus(); // the editor's deferred blur commit does the rest
        }

        private static bool IsInside(DependencyObject node, DependencyObject ancestor)
        {
            for (var current = node; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current == ancestor) return true;
            }
            return false;
        }

        #endregion
    }
}
