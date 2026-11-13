using System;
using System.Reflection;
using System.Reflection.Emit;

// 目的：验证 LanConnectTailMessageRuntime.BindClient(... ReadOnlySpan<byte> ...)
// 能否被 MethodInfo.Invoke 以 byte[] 实参调用。
// 结论用于判定 JoinTakeoverFix.TryBindTail 的反射写法是否可行。
//
// ！本工具只用假类型（下面的 Target），不加载游戏、不加载官方模组、不碰真实的 BindClient。
// ！它验证的是「反射辅助函数在这组形参形态下能不能工作」，**不代表真实类型可用**。
// ！真实类型的接缝是否被打上，由 tools/smoke-test 的 one-real-seam-per-fix 断言覆盖。
// 退出码：0 = 三条可执行实验都得到预期结果；2 = 期望可行的路线（DynamicMethod shim / 强类型委托）失败。

internal sealed class Target
{
    public bool Called;
    public int Length = -1;

    // 与 BindClient 的形参形态一致：ReadOnlySpan<byte> 为 byref-like（ref struct）。
    internal void BindClient(object service, object offer, object selection, ReadOnlySpan<byte> nonce, int? peerTypeId)
    {
        Called = true;
        Length = nonce.Length;
    }
}

internal static class Program
{
    private static int Main()
    {
        object target = new Target();
        MethodInfo bind = typeof(Target).GetMethod("BindClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] nonce = new byte[16];

        Console.WriteLine("NOTE: 本工具只用假类型 Target 验证反射辅助函数，不代表真实类型可用。");
        Console.WriteLine("      (fake type only; this does not prove LanConnectTailMessageRuntime.BindClient works)");
        Console.WriteLine();

        // 期望可行的两条路线：任何一条失败都是异常。
        int anomalies = 0;

        Console.WriteLine("=== 1) MethodInfo.Invoke with byte[] -> ReadOnlySpan<byte> ===");
        try
        {
            bind.Invoke(target, new object?[] { new object(), new object(), new object(), nonce, (int?)53 });
            Console.WriteLine("RESULT: invoke OK (Called=" + ((Target)target).Called + ", len=" + ((Target)target).Length + ")");
        }
        catch (Exception ex)
        {
            Console.WriteLine("RESULT: invoke FAILED -> " + ex.GetType().FullName + ": " + ex.Message);
            if (ex.InnerException != null)
            {
                Console.WriteLine("        inner -> " + ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 2) 装箱 ReadOnlySpan<byte> ===");
        Console.WriteLine("RESULT: 编译期即被拒绝（CS0029：Span/ReadOnlySpan 是 byref-like，不能转换为 object），故该路线不存在。");

        Console.WriteLine();
        Console.WriteLine("=== 3) DynamicMethod shim: byte[] -> ReadOnlySpan<byte> ===");
        try
        {
            DynamicMethod shim = new(
                "BindClientShim",
                typeof(void),
                new[] { typeof(object), typeof(object), typeof(object), typeof(object), typeof(byte[]), typeof(int?) },
                typeof(Program).Module,
                skipVisibility: true);

            ILGenerator il = shim.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, typeof(Target));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Ldarg, 4);          // byte[] nonce
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldarg, 4);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Newobj, typeof(ReadOnlySpan<byte>).GetConstructor(new[] { typeof(byte[]), typeof(int), typeof(int) })!);
            il.Emit(OpCodes.Ldarg, 5);          // int?
            il.Emit(OpCodes.Call, bind);
            il.Emit(OpCodes.Ret);

            Action<object, object, object, object, byte[], int?> invoke =
                (Action<object, object, object, object, byte[], int?>)shim.CreateDelegate(typeof(Action<object, object, object, object, byte[], int?>));
            invoke(target, new object(), new object(), new object(), nonce, 53);
            Console.WriteLine("RESULT: shim OK (Called=" + ((Target)target).Called + ", len=" + ((Target)target).Length + ")");
        }
        catch (Exception ex)
        {
            Console.WriteLine("RESULT: shim FAILED -> " + ex.GetType().FullName + ": " + ex.Message);
            anomalies++;
        }

        Console.WriteLine();
        Console.WriteLine("=== 4) CreateDelegate with a concrete ReadOnlySpan delegate type ===");
        try
        {
            BindSpanDelegate d = (BindSpanDelegate)Delegate.CreateDelegate(typeof(BindSpanDelegate), target, bind);
            d(new object(), new object(), new object(), nonce.AsSpan(), 53);
            Console.WriteLine("RESULT: typed delegate OK (Called=" + ((Target)target).Called + ", len=" + ((Target)target).Length + ")");
        }
        catch (Exception ex)
        {
            Console.WriteLine("RESULT: typed delegate FAILED -> " + ex.GetType().FullName + ": " + ex.Message);
            anomalies++;
        }

        Console.WriteLine();
        Console.WriteLine($"DONE anomalies={anomalies} (fake-type probe; see the NOTE at the top)");
        return anomalies == 0 ? 0 : 2;
    }

    private delegate void BindSpanDelegate(object service, object offer, object selection, ReadOnlySpan<byte> nonce, int? peerTypeId);
}
