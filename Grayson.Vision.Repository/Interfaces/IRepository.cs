// Interfaces/IRepository.cs
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Grayson.Vision.Repository.Interfaces
{
    public interface IRepository<T, TKey> where T : class
    {
        T GetById(TKey id);
        IEnumerable<T> GetAll();
        IEnumerable<T> Find(Expression<Func<T, bool>> predicate);
        bool Insert(T entity);
        bool Update(T entity);
        bool Delete(TKey id);
    }
}