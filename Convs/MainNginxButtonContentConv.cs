using Avalonia.Data.Converters;
using Sheas_Cealer_Nix.Consts;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sheas_Cealer_Nix.Convs;

internal class MainNginxButtonContentConv : IMultiValueConverter
{
    // 新的数据源：IsProxyRunning / IsNginxIniting / IsCoproxyIniting / ProxyEngineName。
    // agent 架构下 GUI 不再按进程名探测，运行状态来自 agent 上报。
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isProxyRunning = (bool)values[0];
        bool isNginxIniting = (bool)values[1];
        bool isCoproxyIniting = (bool)values[2];
        string engineName = values[3] as string ?? "none";

        return isCoproxyIniting ? MainConst.ConginxButtonIsInitingContent :
            isNginxIniting ? MainConst.NginxButtonIsInitingContent :
            isProxyRunning ? MainConst.NginxButtonIsRunningContent : MainConst.NginxButtonIsStoppedContent;
    }
}
