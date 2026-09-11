using Stash.Models;

namespace Stash.ViewModels;

/// <summary>One filter chip in the stash header.</summary>
public sealed class ViewChipViewModel : ObservableObject
{
    private int _count;
    private bool _isActive;

    public ViewChipViewModel(StashView view)
    {
        View = view;
    }

    public StashView View { get; }

    public string Label => View.Label();

    public string IconKey => View.IconKey();

    public int Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value))
            {
                Raise(nameof(CountLabel));
                Raise(nameof(HasCount));
            }
        }
    }

    public string CountLabel => _count.ToString();

    public bool HasCount => _count > 0;

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }
}
