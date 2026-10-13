using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RemoteCli {
internal static class LanNetwork {
    internal static bool Usable(IPAddress address, string name, string description, NetworkInterfaceType type) {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || type == NetworkInterfaceType.Loopback || type == NetworkInterfaceType.Tunnel) return false;
        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return false;
        string adapter = (name + " " + description).ToLowerInvariant();
        return !new[] { "clash", "wintun", "tun adapter", "tap-windows", "vethernet", "virtualbox", "vmware" }.Any(adapter.Contains);
    }
    internal static string Address() {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))))
            foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                if (Usable(ip.Address, nic.Name, nic.Description, nic.NetworkInterfaceType)) return ip.Address.ToString();
        return "";
    }
    static bool Listening(string host, int port) {
        try { using (var client = new TcpClient()) { var attempt = client.BeginConnect(host, port, null, null); using (attempt.AsyncWaitHandle) { if (!attempt.AsyncWaitHandle.WaitOne(1200)) return false; client.EndConnect(attempt); return client.Connected; } } }
        catch (SocketException) { return false; }
    }
    internal static string Diagnose(bool lan, int port, string address) {
        var text = new StringBuilder();
        if (!lan) text.AppendLine("当前没有选择局域网直连。请先在连接页选择“局域网”并连接，再用新的二维码扫码。");
        text.AppendLine("局域网地址：" + (address.Length > 0 ? address + ":" + port : "未找到可用的 Wi-Fi / 网线地址"));
        bool local = Listening("127.0.0.1", port);
        text.AppendLine("本机端口：" + (local ? "可以连接" : "没有响应，请检查连接状态或重新连接"));
        if (local && address.Length > 0) text.AppendLine("本机访问局域网地址：" + (Listening(address, port) ? "可以连接（仍需手机验证入站连接）" : "失败，请检查监听地址、防火墙和代理路由"));
        bool proxy = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up && (n.Name + n.Description).ToLowerInvariant().Contains("clash"));
        if (proxy) text.AppendLine("检测到 Clash 虚拟网卡；二维码已避开其虚拟地址。开启代理本身不能证明它是故障原因。");
        text.AppendLine("\r\n手机仍连不上时：\r\n1. Windows 防火墙需允许本程序内置的 python.exe 接收入站连接；校园网常被识别为公用网络。\r\n2. 检查手机和电脑的代理是否绕过这台电脑的局域网地址，可短暂关闭代理后对比。\r\n3. 同一个校园 Wi-Fi 不保证设备互通。若校园网隔离了设备，可改用手机热点或公共中转。\r\n不必关闭整个防火墙。");
        return text.ToString();
    }
}
}
