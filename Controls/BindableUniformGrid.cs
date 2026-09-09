using System.Windows;
using System.Windows.Controls.Primitives;

namespace FoldRa.Controls
{
    /// <summary>
    /// UniformGrid with bindable Columns property.
    /// Solves the issue where standard UniformGrid.Columns cannot be bound from ItemsPanelTemplate.
    /// </summary>
    public class BindableUniformGrid : UniformGrid
    {
        /// <summary>
        /// Bindable Columns dependency property
        /// </summary>
        public static readonly DependencyProperty BindableColumnsProperty =
            DependencyProperty.Register(
                nameof(BindableColumns),
                typeof(int),
                typeof(BindableUniformGrid),
                new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure, OnBindableColumnsChanged));

        /// <summary>
        /// Gets or sets the number of columns - bindable from XAML
        /// </summary>
        public int BindableColumns
        {
            get => (int)GetValue(BindableColumnsProperty);
            set => SetValue(BindableColumnsProperty, value);
        }

        private static void OnBindableColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UniformGrid grid && e.NewValue is int columns)
            {
                grid.Columns = columns;
            }
        }

        public BindableUniformGrid()
        {
            // Initialize with default columns
            Columns = 3;
        }
    }
}


