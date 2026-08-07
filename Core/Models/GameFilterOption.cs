using System.ComponentModel;
using Core.Enums;

namespace Core.Models
{
    /// <summary>
    /// Represents a selectable filter option for a specific game platform, including its display name, unique
    /// identifier, and selection state.
    /// </summary>
    /// <remarks>This class is typically used in user interfaces to allow users to filter games by platform.
    /// It implements INotifyPropertyChanged to support data binding scenarios where changes to the selection state
    /// should be reflected in the UI.</remarks>
    public sealed class GameFilterOption : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isExcluded;

        /// <summary>
        /// Occurs when a property value changes.
        /// </summary>
        /// <remarks>This event is typically raised by classes that implement the INotifyPropertyChanged
        /// interface to notify clients, such as data-binding frameworks, that a property value has changed. Handlers
        /// attached to this event receive the name of the property that changed in the PropertyChangedEventArgs
        /// parameter.</remarks>
        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>
        /// Gets the platform on which the application is running.
        /// </summary>
        public Platform Platform { get; }
        /// <summary>
        /// Gets the URL-friendly identifier for the resource.
        /// </summary>
        public string Slug { get; }
        /// <summary>
        /// Gets the display name associated with the object.
        /// </summary>
        public string DisplayName { get; }
        /// <summary>
        /// Gets or sets a value indicating whether the item is selected.
        /// </summary>
        /// <remarks>Setting this property raises the PropertyChanged event for data binding scenarios.
        /// This property is typically used in selection models or UI elements to track user selection state.</remarks>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                if (value && _isExcluded)
                {
                    // "Mine this" and "Exclude" are mutually exclusive per game.
                    _isExcluded = false;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExcluded)));
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        /// <summary>
        /// Gets or sets whether this game is explicitly EXCLUDED from mining. Independent of <see cref="IsSelected"/>
        /// and always wins: an excluded game is never mined, even if it is also on the allow-list. This is the
        /// per-game "block this game" switch, as opposed to the old all-or-nothing exclude mode.
        /// </summary>
        public bool IsExcluded
        {
            get => _isExcluded;
            set
            {
                if (_isExcluded == value)
                    return;

                _isExcluded = value;
                if (value && _isSelected)
                {
                    _isSelected = false;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExcluded)));
            }
        }
        /// <summary>
        /// Initializes a new instance of the GameFilterOption class with the specified platform, slug, display name,
        /// and selection state.
        /// </summary>
        /// <param name="platform">The platform associated with this filter option.</param>
        /// <param name="slug">The unique identifier used to reference this filter option.</param>
        /// <param name="displayName">The display name shown to users for this filter option.</param>
        /// <param name="isSelected">A value indicating whether this filter option is initially selected.</param>
        public GameFilterOption(Platform platform, string slug, string displayName, bool isSelected, bool isExcluded = false)
        {
            Platform = platform;
            Slug = slug;
            DisplayName = displayName;
            _isSelected = isSelected;
            _isExcluded = isExcluded;
        }
    }
}