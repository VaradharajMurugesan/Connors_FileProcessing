using Polly;
using System;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.Helpers
{
    public class RetryHelper
    {
        public static async Task<T> RetryAsync<T>(Func<Task<T>> action, int maxRetryAttempts = 3)
        {
            var retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(maxRetryAttempts, retryAttempt => TimeSpan.FromSeconds(10));

            return await retryPolicy.ExecuteAsync(action);
        }

        public static async Task RetryAsync(Func<Task> action, int maxRetryAttempts = 3)
        {
            var retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(maxRetryAttempts, retryAttempt => TimeSpan.FromSeconds(10));

            await retryPolicy.ExecuteAsync(action);
        }
    }
}
