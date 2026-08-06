namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// 页面或ViewModel导航生命周期感知接口
    /// </summary>
    public interface INavigationAware
    {
        /// <summary>
        /// 导航进入时触发（用于接收传递参数并加载数据）
        /// </summary>
        void OnNavigatedTo(object parameter);

        /// <summary>
        /// 导航离开时触发（用于保存数据或清理资源）
        /// </summary>
        void OnNavigatedFrom();
    }
}