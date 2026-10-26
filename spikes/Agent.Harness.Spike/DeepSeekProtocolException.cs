namespace Agent.Harness.Spike;

/// <summary>供应商返回了无法解析的流或 Schema。</summary>
internal sealed class DeepSeekProtocolException(string message) : Exception(message);
