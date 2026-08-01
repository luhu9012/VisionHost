using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImageInput.ImageChannel
{
    public class ImageChannelParam : ParamBase
    {
        private string _colorSpace = "RGB"; // RGB, HSV
        public string ColorSpace
        {
            get => _colorSpace;
            set => Set(ref _colorSpace, value);
        }

        private string _operationType = "Split"; // Split, Combine
        public string OperationType
        {
            get => _operationType;
            set => Set(ref _operationType, value);
        }
    }
}