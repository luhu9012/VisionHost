using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.FlowControl.ConditionIf
{
    public class ConditionIfParam : ParamBase
    {
        private bool _useExpression = false;
        /// <summary>
        /// 是否启用高级表达式模式
        /// </summary>
        public bool UseExpression
        {
            get => _useExpression;
            set => Set(ref _useExpression, value);
        }

        private string _expression = "InputVal >= 0.8";
        /// <summary>
        /// 布尔表达式 (支持 &&, ||, !, >, <, == 等)
        /// </summary>
        public string Expression
        {
            get => _expression;
            set => Set(ref _expression, value);
        }

        private string _inputVariableName = "Value";
        public string InputVariableName
        {
            get => _inputVariableName;
            set => Set(ref _inputVariableName, value);
        }

        private string _operator = "==";
        public string Operator
        {
            get => _operator;
            set => Set(ref _operator, value);
        }

        private string _compareValue = "true";
        public string CompareValue
        {
            get => _compareValue;
            set => Set(ref _compareValue, value);
        }

        #region 参数校验逻辑
        public override string this[string columnName]
        {
            get
            {
                if (UseExpression)
                {
                    if (columnName == nameof(Expression) && string.IsNullOrWhiteSpace(Expression))
                        return "条件表达式不能为空";
                }
                else
                {
                    if (columnName == nameof(InputVariableName) && string.IsNullOrWhiteSpace(InputVariableName))
                        return "判断变量标识不能为空";
                    if (columnName == nameof(CompareValue) && CompareValue == null)
                        return "比较目标值不能为 null";
                }
                return null;
            }
        }
        #endregion
    }
}