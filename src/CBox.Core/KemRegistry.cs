namespace CBox.Core;

/// <summary>
/// 公钥封装算法的注册表。**当前为空**——为格式 v2 预留。
///
/// 与 <see cref="CipherRegistry"/> / <see cref="KdfRegistry"/> 同样的模式：
/// 新算法 = 实现 <see cref="IKem"/> + 在这里注册一行，其余代码不用改。
///
/// 一旦有实现，格式需要升到 v2（因为要在头部追加 KEM 相关字段），
/// 那一步会同时决定老文件的兼容策略——见 logs\02-格式规格.md 的 v2 规划。
/// </summary>
public static class KemRegistry
{
    private static readonly Dictionary<byte, IKem> Items = new();

    public static void Register(IKem kem)
    {
        Items[kem.Id] = kem;
    }

    /// <summary>按编号取实现。没有就是"这个版本还不支持"，直接报错而不是静默降级。</summary>
    public static IKem Get(byte id)
    {
        if (!Items.TryGetValue(id, out var kem))
        {
            throw new CbxException(
                $"这个文件使用了公钥封装（编号 {id}），但当前版本还不支持。请升级程序。");
        }

        return kem;
    }

    public static bool IsSupported(byte id) => Items.ContainsKey(id);

    public static IReadOnlyCollection<IKem> All => Items.Values;
}
