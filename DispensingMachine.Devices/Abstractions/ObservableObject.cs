using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DispensingMachine;

/// <summary>
/// 可观察对象基类。
/// 设备状态是被后台逻辑持续改写的，界面必须能感知变化，所以模型本身实现 INotifyPropertyChanged。
/// 这里手写而不是用 Prism 的 BindableBase，是为了让 Devices 层不依赖任何 UI 框架 ——
/// 换界面框架（WPF/Avalonia/控制台）时这一层不用动。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
