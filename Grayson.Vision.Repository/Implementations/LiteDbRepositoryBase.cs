// Implementations/LiteDbRepositoryBase.cs
using Grayson.Vision.Repository.Core;
using Grayson.Vision.Repository.Interfaces;
using LiteDB;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Grayson.Vision.Repository.Implementations
{
    public abstract class LiteDbRepositoryBase<T> : IRepository<T, string> where T : BaseEntity
    {
        protected abstract string CollectionName { get; }

        public virtual T GetById(string id)
        {
            using (var db = DbContext.GetDatabase())
            {
                return db.GetCollection<T>(CollectionName).FindById(id);
            }
        }

        public virtual IEnumerable<T> GetAll()
        {
            using (var db = DbContext.GetDatabase())
            {
                return db.GetCollection<T>(CollectionName).FindAll();
            }
        }

        public virtual IEnumerable<T> Find(Expression<Func<T, bool>> predicate)
        {
            using (var db = DbContext.GetDatabase())
            {
                return db.GetCollection<T>(CollectionName).Find(predicate);
            }
        }

        public virtual bool Insert(T entity)
        {
            if (string.IsNullOrEmpty(entity.Id))
            {
                entity.Id = Guid.NewGuid().ToString("N");
            }
            entity.CreatedTime = DateTime.Now;
            entity.UpdatedTime = DateTime.Now;

            using (var db = DbContext.GetDatabase())
            {
                return db.GetCollection<T>(CollectionName).Insert(entity) != null;
            }
        }

        public virtual bool Update(T entity)
        {
            entity.UpdatedTime = DateTime.Now;
            using (var db = DbContext.GetDatabase())
            {
                return db.GetCollection<T>(CollectionName).Update(entity);
            }
        }

        public virtual bool Delete(string id)
        {
            using (var db = DbContext.GetDatabase())
            {
                return db.GetCollection<T>(CollectionName).Delete(id);
            }
        }
    }
}