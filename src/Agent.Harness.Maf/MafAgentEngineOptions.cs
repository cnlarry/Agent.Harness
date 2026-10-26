namespace Agent.Harness.Maf;

/// <summary>
/// MAF 引擎的工具选择与并发选项。直接构造 <see cref="MafAgentEngine"/> 时保持旧行为
/// （全部工具、串行）；通过 <c>AddAgentHarnessMaf</c> 注册时默认只暴露只读工具。
/// </summary>
public sealed class MafAgentEngineOptions
{
    /// <summary>
    /// 只向模型暴露 <c>ReadOnly=true</c> 且 <c>Risk=Read</c> 的工具。
    /// 读路径灰度默认开启；写路径阶段再显式关闭。
    /// </summary>
    public bool ReadOnlyToolsOnly { get; set; }

    /// <summary>可选的工具名 allowlist；为 null 时按 <see cref="ReadOnlyToolsOnly"/> 决定；空数组表示不暴露工具。</summary>
    public string[]? EnabledTools { get; set; }

    /// <summary>是否允许同一轮里的多个工具并发执行；默认 false。</summary>
    public bool AllowConcurrentToolInvocation { get; set; }

    /// <summary>
    /// 空正文、无工具调用、正常结束时的最大重试次数。默认 1；0 表示不重试。
    /// </summary>
    public int MaxEmptyResponseRetries { get; set; } = 1;

    /// <summary>
    /// 达到工具轮上限时追加给模型的 system 说明（默认 null = 不追加）。
    ///
    /// <para>
    /// 说明文本属提示词，由宿主持有：只有宿主知道该用什么措辞、要不要提"下一步怎么办"。
    /// 这一轮同时会撤掉工具声明——不发工具还留着"你可以调用"的通道，模型就会把调用写进正文。
    /// </para>
    ///
    /// <para>
    /// 可经配置绑定（<c>AgentHarness:Maf:FinalRoundInstruction</c>）。
    /// </para>
    /// </summary>
    public string? FinalRoundInstruction { get; set; }
}
