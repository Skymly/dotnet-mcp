namespace CsConsumer;

public static class Use
{
    public static int Apply(int n) => FsOnlyCs.Scaling.scale(n);
    private static int scale(int n) => n;
    public static int Local(int n) => scale(n);
}
