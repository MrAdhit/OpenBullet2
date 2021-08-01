﻿using OpenBullet2.Core.Repositories;
using RuriLib.Models.Proxies;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenBullet2.Core.Models.Proxies
{
    /// <summary>
    /// An proxy check output that writes proxies to an <see cref="IProxyRepository"/>.
    /// </summary>
    public class DatabaseProxyCheckOutput : IProxyCheckOutput, IDisposable
    {
        private readonly IProxyRepository proxyRepo;
        private readonly SemaphoreSlim semaphore;

        public DatabaseProxyCheckOutput(IProxyRepository proxyRepo)
        {
            this.proxyRepo = proxyRepo;
            semaphore = new SemaphoreSlim(1, 1);
        }

        /// <inheritdoc/>
        public async Task Store(Proxy proxy)
        {
            try
            {
                var entity = await proxyRepo.Get(proxy.Id);
                entity.Country = proxy.Country;
                entity.LastChecked = proxy.LastChecked;
                entity.Ping = proxy.Ping;
                entity.Status = proxy.WorkingStatus;

                // Only allow updating one proxy at a time (multiple threads should
                // not use the same DbContext at the same time).
                await semaphore.WaitAsync();

                try
                {
                    await proxyRepo.Update(entity);
                }
                finally
                {
                    semaphore.Release();
                }
            }
            catch (ObjectDisposedException)
            {
                // If we are here, it means we deleted the job but the parallelizer was still running
                // so we don't need to put anything in the database and we can safely ignore the exception.
            }
        }

        public void Dispose()
        {
            semaphore?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
