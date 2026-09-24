namespace XiControl.SystemIntegration;

/// <summary>
/// Общий источник касаний тачпада (XIC-78): один <see cref="RawTouchpadReader"/> на процесс,
/// кадры раздаются всем подписчикам. Делить нужно не ради экономии: Raw Input регистрирует
/// у процесса ровно одно окно-получатель на пару «страница/usage», и второй независимый
/// читатель молча отобрал бы ввод у первого — краевые ползунки или сильное нажатие перестали
/// бы видеть касания без всякой ошибки.
///
/// Читатель живёт, пока нужен хоть кому-то: <see cref="Acquire"/>/<see cref="Release"/> —
/// счётчик пользователей, а не флаг. Кадры приходят с потока читателя; подписчики держат
/// обработчики лёгкими — очередь сообщений под ними одна на всех.
/// </summary>
public sealed class TouchpadInput : IDisposable
{
    private readonly RawTouchpadReader _reader;
    private readonly object _lock = new();
    private int _users;
    private int _pressureUsers;

    /// <summary>Кадр касаний (поток читателя!). Пустой список — все пальцы оторваны.</summary>
    public event Action<IReadOnlyList<TouchContact>>? Frame;

    public TouchpadInput() => _reader = new RawTouchpadReader(f => Frame?.Invoke(f));

    /// <summary>Мне нужны касания: первый пользователь поднимает чтение. <paramref name="pressure"/>
    /// — нужно ли давление: без него поле не разбирается вовсе.</summary>
    public void Acquire(bool pressure = false)
    {
        lock (_lock)
        {
            if (pressure && _pressureUsers++ == 0) _reader.ReadPressure = true;
            if (_users++ == 0) _reader.Start();
        }
    }

    /// <summary>Касания больше не нужны: последний пользователь гасит чтение, последний
    /// нуждавшийся в давлении — его разбор. Флаг — тот же, что при Acquire.</summary>
    public void Release(bool pressure = false)
    {
        lock (_lock)
        {
            if (pressure && _pressureUsers > 0 && --_pressureUsers == 0) _reader.ReadPressure = false;
            if (_users == 0) return;
            if (--_users == 0) _reader.Stop();
        }
    }

    public void Dispose() => _reader.Dispose();
}
