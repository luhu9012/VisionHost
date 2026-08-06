// Interfaces/IUserRepository.cs
using Grayson.Vision.Repository.Entities;

namespace Grayson.Vision.Repository.Interfaces
{
    public interface IUserRepository : IRepository<UserPo, string>
    {
        UserPo GetByUsername(string username);
        bool ValidateUser(string username, string passwordHash);
    }
}