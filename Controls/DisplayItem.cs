using System.ComponentModel;
using System.Windows.Media;

namespace FoldRa.Controls
{
    /// <summary>
    /// Display item model for the ItemsControl binding
    /// </summary>
    public class DisplayItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int Index { get; set; }
        
        private ImageSource _icon;
        public ImageSource Icon 
        { 
            get => _icon; 
            set 
            { 
                _icon = value; 
                OnPropertyChanged(nameof(Icon)); 
            } 
        }
        
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
        
        private Brush _textColor;
        public Brush TextColor
        {
            get => _textColor;
            set
            {
                _textColor = value;
                OnPropertyChanged(nameof(TextColor));
            }
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}


