using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// POST /futures/{settle}/orders 请求体（仅下单用到的字段）。
/// size 为带符号整数张：正=买/开多，负=卖/开空（§7 数量语义：币→张换算在适配器内完成）。
/// 市价单协议形态：price "0" + tif ioc。
/// reduce_only 仅双向持仓（dual）减仓单使用；single 模式不传（null 不序列化）。
/// </summary>
internal sealed record GateFuturesOrderRequest(
    string Contract,
    long Size,
    string Price,
    string Tif,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ReduceOnly = null);

/// <summary>
/// POST /futures/{settle}/orders 响应（201，录制形态见 .local/gate_api_futures_p_restful.md）。
/// id/size/left 文档与 testnet 形态不一（裸数字与字符串均出现），AllowReadingFromString 双态通吃（同 GateFuturesContract 先例）。
/// status 两态 open/finished；finished 的细分看 finish_as。create_time 为秒（可小数）。
/// </summary>
internal sealed record GateFuturesOrder(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long Id,
    string Status,
    string Contract,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long Size,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long Left,
    string? Price,
    string? FinishAs,
    double CreateTime);
