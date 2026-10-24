#nullable enable
using System;
using System.Threading.Tasks;

namespace SlabReinforcement
{
    internal static class StartupAsyncRunner
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

        internal static string? Start(Func<Task> action, TimeSpan? timeout = null, Action<string>? warningSink = null)
        {
            if (action == null)
            {
                return "Сервисный запуск фоновой задачи не настроен.";
            }

            try
            {
                TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Task actionTask = action();
                        Task completedTask = await Task.WhenAny(actionTask, Task.Delay(effectiveTimeout)).ConfigureAwait(false);
                        if (completedTask == actionTask)
                        {
                            await actionTask.ConfigureAwait(false);
                        }
                        else
                        {
                            warningSink?.Invoke($"Сервисная фоновая задача не завершилась за {effectiveTimeout.TotalSeconds:0.#} c.");
                        }
                    }
                    catch (Exception ex)
                    {
                        warningSink?.Invoke($"Сервисная фоновая задача завершилась ошибкой: {ex.Message}");
                    }
                });

                return null;
            }
            catch (Exception ex)
            {
                return $"Не удалось запустить сервисную фоновую задачу: {ex.Message}";
            }
        }
    }
}
