namespace Grayson.Vision.Nodes.Logic.Merge
{
    public enum MergeDataMode
    {
        FirstNonNull, // 优先使用第一个非空输入（适用于 If-Else 条件分支汇聚）
        CollectList,   // 汇总追加为列表 List<object>（适用于 ForLoop 循环数据收集）
        PassThrough    // 透传最新到达的数据
    }

    public class MergeParam
    {
        public MergeDataMode DataMode { get; set; } = MergeDataMode.FirstNonNull;
        public bool ClearListOnStart { get; set; } = true;
    }
}