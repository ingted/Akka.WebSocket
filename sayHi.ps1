using namespace System.Net.WebSockets
using namespace System.Threading
using namespace System.Text

$uri = "ws://localhost:8080/ws"
$ws  = [ClientWebSocket]::new()
$ct  = [CancellationToken]::None

# 連線
$ws.ConnectAsync([Uri]$uri, $ct).Wait()
Write-Host "✅ Connected to $uri"

# 傳送訊息（對應 ws.onopen → send("hi")）
$msg = "hi"
$bytes = [Encoding]::UTF8.GetBytes($msg)
$seg = [ArraySegment[byte]]::new($bytes)
$ws.SendAsync($seg, [WebSocketMessageType]::Text, $true, $ct).Wait()
Write-Host "➡️ Sent: $msg"

# 接收訊息（對應 ws.onmessage）
Start-Job -ScriptBlock {
    param($ws)
    $ct = [Threading.CancellationToken]::None
    $buf = New-Object byte[] 4096
    $seg = [ArraySegment[byte]]::new($buf)

    while ($ws.State -eq [WebSocketState]::Open) {
        $res = $ws.ReceiveAsync($seg, $ct).Result
        if ($res.MessageType -eq [WebSocketMessageType]::Close) {
            Write-Host "🔒 Server closed connection."
            break
        }
        $msg = [Encoding]::UTF8.GetString($buf, 0, $res.Count)
        Write-Host "📨 msg $msg"
    }
} -ArgumentList $ws | Out-Null