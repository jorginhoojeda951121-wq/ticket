using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IRCTCTatkalBot.Models
{
    /// <summary>
    /// Represents a passenger to be booked on a train (editable in UI).
    /// </summary>
    public class Passenger : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private int _age;
        private string _gender = "M";
        private string _berthPreference = "NO";
        private string _idType = "PAN";
        private string _idNumber = string.Empty;
        private bool _isSeniorCitizen;
        private string _nationality = "IN";

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public int Age
        {
            get => _age;
            set { _age = value; OnPropertyChanged(); }
        }

        public string Gender
        {
            get => _gender;
            set { _gender = value; OnPropertyChanged(); }
        }

        public string BerthPreference
        {
            get => _berthPreference;
            set { _berthPreference = value; OnPropertyChanged(); }
        }

        public string IdType
        {
            get => _idType;
            set { _idType = value; OnPropertyChanged(); }
        }

        public string IdNumber
        {
            get => _idNumber;
            set { _idNumber = value; OnPropertyChanged(); }
        }

        public bool IsSeniorCitizen
        {
            get => _isSeniorCitizen;
            set { _isSeniorCitizen = value; OnPropertyChanged(); }
        }

        public string Nationality
        {
            get => _nationality;
            set { _nationality = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
