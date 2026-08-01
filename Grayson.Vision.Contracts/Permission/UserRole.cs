namespace Grayson.Vision.Contracts.Permission
{
    /// <summary>系统三级角色权限划分，控制菜单、编辑功能显隐</summary>
    public enum UserRole
    {
        /// <summary>操作员：仅可切换已有配方，不能编辑流程、参数</summary>
        Operator = 1,

        /// <summary>工程师：可编辑流程、调参、标定，不可修改账号权限</summary>
        Engineer = 2,

        /// <summary>管理员：全功能开放，权限、硬件配置均可修改</summary>
        Administrator = 3
    }
}