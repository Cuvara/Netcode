namespace Cuvara.Netcode.Json
{
    /// <summary>The seven JSON value kinds, plus <see cref="Null"/>.</summary>
    public enum JsonKind
    {
        Null = 0,
        Bool = 1,
        Number = 2,
        String = 3,
        Array = 4,
        Object = 5
    }
}
