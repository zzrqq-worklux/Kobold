using System;
using System.Windows.Media;

namespace FoldRa.Core
{
    /// <summary>
    /// Centralized theme management with event-driven updates
    /// </summary>
    public static class ThemeManager
    {
        #region Theme Changed Event
        
        /// <summary>
        /// Fired when theme changes - widgets subscribe to this for automatic updates
        /// </summary>
        public static event Action ThemeChanged;
        
        #endregion
        
        #region Theme State
        
        private static bool _isDarkTheme = true;
        
        /// <summary>
        /// Returns true if current theme is dark
        /// </summary>
        public static bool IsDarkTheme => _isDarkTheme;
        
        /// <summary>
        /// Sets the theme and notifies all subscribers
        /// </summary>
        public static void SetTheme(string theme)
        {
            bool newIsDark = theme == "dark";
            if (_isDarkTheme == newIsDark) return;
            
            _isDarkTheme = newIsDark;
            UpdateBrushes();
            ThemeChanged?.Invoke();
        }
        
        /// <summary>
        /// Initialize theme from config (call once at startup)
        /// </summary>
        public static void Initialize(string theme)
        {
            _isDarkTheme = theme == "dark";
            UpdateBrushes();
        }
        
        #endregion
        
        #region Cached Brushes (Frozen for thread-safety and performance)
        
        public static SolidColorBrush TextBrush { get; private set; }
        public static SolidColorBrush SecondaryTextBrush { get; private set; }
        public static SolidColorBrush PanelHeaderBrush { get; private set; }
        
        private static void UpdateBrushes()
        {
            TextBrush = CreateFrozen(_isDarkTheme ? Colors.White : Color.FromRgb(40, 40, 40));
            SecondaryTextBrush = CreateFrozen(_isDarkTheme ? Color.FromRgb(180, 180, 180) : Color.FromRgb(100, 100, 100));
            PanelHeaderBrush = CreateFrozen(_isDarkTheme ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        }
        
        private static SolidColorBrush CreateFrozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        
        #endregion
        
        #region Color Properties (for panel backgrounds, etc.)
        
        public static Color PanelBackground => _isDarkTheme 
            ? Color.FromRgb(40, 40, 40) 
            : Color.FromRgb(250, 250, 250);
            
        public static Color TextColor => _isDarkTheme 
            ? Colors.White 
            : Color.FromRgb(40, 40, 40);
            
        public static Color ItemHoverBackground => _isDarkTheme 
            ? Color.FromArgb(100, 255, 255, 255) 
            : Color.FromArgb(40, 0, 0, 0);
        
        #endregion
        
        #region Static Color Constants
        
        // Menu colors (always dark)
        public static readonly Color MenuBackground = Color.FromRgb(40, 40, 40);
        public static readonly Color MenuBorder = Color.FromRgb(60, 60, 60);
        public static readonly Color MenuHover = Color.FromRgb(60, 60, 60);
        
        // Dialog colors (always dark)
        public static readonly Color DialogBackground = Color.FromRgb(30, 30, 30);
        public static readonly Color DialogBorder = Color.FromRgb(60, 60, 60);
        public static readonly Color InputBackground = Color.FromRgb(45, 45, 45);
        public static readonly Color InputBorder = Color.FromRgb(70, 70, 70);
        
        // Accent colors
        public static readonly Color AccentBlue = Color.FromRgb(59, 130, 246);
        
        #endregion
        
        #region Frozen Brush Cache (Memory Optimization)
        
        // Common UI brushes - frozen for thread-safety and zero allocation
        public static readonly SolidColorBrush WhiteBrush = Freeze(Colors.White);
        public static readonly SolidColorBrush TransparentBrush = Freeze(Colors.Transparent);
        public static readonly SolidColorBrush AccentBlueBrush = Freeze(AccentBlue);
        
        // Dialog brushes
        public static readonly SolidColorBrush DialogBackgroundBrush = Freeze(DialogBackground);
        public static readonly SolidColorBrush DialogBorderBrush = Freeze(DialogBorder);
        public static readonly SolidColorBrush InputBackgroundBrush = Freeze(InputBackground);
        public static readonly SolidColorBrush InputBorderBrush = Freeze(InputBorder);
        
        // Menu brushes
        public static readonly SolidColorBrush MenuBackgroundBrush = Freeze(MenuBackground);
        public static readonly SolidColorBrush MenuBorderBrush = Freeze(MenuBorder);
        public static readonly SolidColorBrush MenuHoverBrush = Freeze(MenuHover);
        
        // Hover brushes (theme-independent)
        public static readonly SolidColorBrush DarkHoverBrush = Freeze(Color.FromArgb(35, 255, 255, 255));
        public static readonly SolidColorBrush LightHoverBrush = Freeze(Color.FromArgb(25, 0, 0, 0));
        
        // Pin button colors
        public static readonly SolidColorBrush PinGoldBrush = Freeze(Color.FromRgb(0xFF, 0xD7, 0x00));
        public static readonly SolidColorBrush PinDarkGoldBrush = Freeze(Color.FromRgb(0xB8, 0x86, 0x0B));
        public static readonly SolidColorBrush PinUnpinnedFillBrush = Freeze(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        public static readonly SolidColorBrush PinUnpinnedStrokeBrush = Freeze(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        
        // Common gray brushes
        public static readonly SolidColorBrush Gray45Brush = Freeze(Color.FromRgb(45, 45, 45));
        public static readonly SolidColorBrush Gray60Brush = Freeze(Color.FromRgb(60, 60, 60));
        public static readonly SolidColorBrush Gray70Brush = Freeze(Color.FromRgb(70, 70, 70));
        public static readonly SolidColorBrush Gray200Brush = Freeze(Color.FromRgb(200, 200, 200));
        
        // Current theme hover brush
        public static SolidColorBrush HoverBrush => _isDarkTheme ? DarkHoverBrush : LightHoverBrush;
        
        private static SolidColorBrush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        
        #endregion
        
        #region Legacy Support (backwards compatibility)
        
        /// <summary>
        /// Applies theme to all widgets - legacy method, use SetTheme instead
        /// </summary>
        public static void ApplyTheme()
        {
            if (WidgetManager.Instance == null) return;
            
            foreach (var widget in WidgetManager.Instance.Widgets)
            {
                widget.RefreshTheme();
            }
        }
        
        #endregion
    }
}


