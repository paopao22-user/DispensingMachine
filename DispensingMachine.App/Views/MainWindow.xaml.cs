using System.Windows;
using DispensingMachine.App.ViewModels;

namespace DispensingMachine.App.Views;

/// <summary>
/// 主窗口代码后置。
///
/// 注意这里【几乎什么都没有】—— 没有按钮点击事件、没有业务逻辑、没有控件操作。
/// 唯一的代码是 Loaded 时通知 ViewModel 去连接 PLC。
/// 这就是 MVVM 想要的效果：界面文件只负责"长什么样"，不负责"做什么"。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.InitializeAsync();
    }
}
