using System.Windows.Controls;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vison.FlowEdit.ViewModels; // 引入 FlowVm 命名空间

namespace Grayson.Vision.WpfUI.View
{
    public partial class FlowEditView : UserControl, INavigationAware
    {
        private RecipeModel _currentRecipe;

        public FlowEditView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 🌟 ViewModel 快捷获取属性
        /// </summary>
        private FlowVm ViewModel => DataContext as FlowVm;

        /// <summary>
        /// 🌟 跨界面跳转进入时触发参数接收与编辑器加载
        /// </summary>
        public void OnNavigatedTo(object parameter)
        {
            if (parameter is RecipeModel recipe)
            {
                _currentRecipe = recipe;

                // 优先通过 View 的 DataContext (FlowVm) 进行完整的配方实体加载
                if (ViewModel != null)
                {
                    ViewModel.LoadRecipe(_currentRecipe);
                }
                
            }
        }

        /// <summary>
        /// 🌟 离开页面时同步并保存最新的流程变更
        /// </summary>
        public void OnNavigatedFrom()
        {
            // 退出页面时从 FlowVm 导出最新数据
            if (ViewModel != null)
            {
                _currentRecipe = ViewModel.ExportCurrentRecipe();
            }
            
        }
    }
}