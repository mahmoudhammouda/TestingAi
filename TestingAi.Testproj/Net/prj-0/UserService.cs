using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TestingAi.Testproj.Net.Prj0
{
    public interface IUserRepository
    {
        Task<User> GetByIdAsync(int id);
        Task SaveAsync(User user);
    }

    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    public class UserService
    {
        private readonly IUserRepository _repository;

        public UserService(IUserRepository repository)
        {
            _repository = repository;
        }

        public async Task<User> GetUserAsync(int id)
        {
            if (id <= 0) throw new ArgumentException("ID invalide");
            return await _repository.GetByIdAsync(id);
        }

        public async Task UpdateUserNameAsync(int id, string newName)
        {
            var user = await _repository.GetByIdAsync(id);
            if (user == null) return;
            
            user.Name = newName;
            await _repository.SaveAsync(user);
        }
    }
}
