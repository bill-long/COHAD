using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;

namespace Web.Repository
{
    public class AzureTableRepository<T> where T : ITableEntity, new()
    {
        private readonly CloudTable _table;

        public AzureTableRepository(CloudTable table)
        {
            _table = table;
        }

        public async Task<T> Add(T entity)
        {
            var op = TableOperation.Insert(entity);
            var result = await _table.ExecuteAsync(op);

            return (T)result.Result;
        }

        public async Task<IEnumerable<T>> FindAll()
        {
            var results = new List<T>();
            var query = new TableQuery<T>();
            TableContinuationToken token = null;
            do
            {
                var segment = await _table.ExecuteQuerySegmentedAsync(query, token);
                token = segment.ContinuationToken;
                results.AddRange(segment.Results);
            } while (token != null);

            return results;
        }

        public async Task<T> FindByKey(string key)
        {
            var op = TableOperation.Retrieve<T>(key, "");
            var result = await _table.ExecuteAsync(op);
            return (T)result.Result;
        }

        public async Task<T> Replace(T entity)
        {
            entity.ETag = "*";
            var op = TableOperation.Replace(entity);
            var result = await _table.ExecuteAsync(op);
            return (T) result.Result;
        }

        public async Task Delete(T entity)
        {
            var op = TableOperation.Delete(entity);
            var result = await _table.ExecuteAsync(op);
        }
    }
}