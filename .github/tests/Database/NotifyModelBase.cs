using System.ComponentModel;

namespace AllLive.UWP.ViewModels
{
    // The model's notification base is the only UWP view-model dependency of the database DTOs.
    public class BaseNotifyPropertyChanged : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public void DoPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
