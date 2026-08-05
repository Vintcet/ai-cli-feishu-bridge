namespace AiCliFeishu.Bridge.Core;

public sealed record StateTransition<TState, TValue>(TState State, TValue Value);
