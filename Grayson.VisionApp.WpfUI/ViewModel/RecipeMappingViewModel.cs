using System.Collections.ObjectModel;
using System.Windows.Input;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class RecipeMappingViewModel : ViewModelBase
    {
        private readonly StationManageViewModel _parent;

        public RecipeMappingViewModel(StationManageViewModel parent)
        {
            _parent = parent;
            // expose commands as thin wrappers if needed
        }

        public ObservableCollection<RecipeModel> AvailableRecipes => _parent.AvailableRecipes;
        public StationModel SelectedStation => _parent.SelectedStation;

        // Example command placeholders
        public ICommand SaveMappingCommand => _parent.SaveStationConfigCommand;
    }
}
