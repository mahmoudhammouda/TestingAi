using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Moq;
using FluentAssertions;
using TestingAi.Testproj.Net.Prj0;

namespace TestingAi.Testproj.Net.Prj0.Tests
{
    public class UserServiceTests
    {
        private readonly Mock<IUserRepository> _userRepositoryMock;
        private readonly UserService _userService;

        public UserServiceTests()
        {
            _userRepositoryMock = new Mock<IUserRepository>();
            _userService = new UserService(_userRepositoryMock.Object);
        }

        #region GetUserAsync Tests

        [Fact]
        public async Task GetUserAsync_WithValidExistingId_ReturnsExpectedUser()
        {
            // Arrange
            int userId = 42;
            var expectedUser = new User { Id = userId, Name = "Alice" };
            
            _userRepositoryMock
                .Setup(repo => repo.GetByIdAsync(userId))
                .ReturnsAsync(expectedUser);

            // Act
            var result = await _userService.GetUserAsync(userId);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().Be(userId);
            result.Name.Should().Be("Alice");
        }

        [Fact]
        public async Task GetUserAsync_WithMaxIntValue_ReturnsUserOrNull()
        {
            // Arrange
            int userId = int.MaxValue;
            var expectedUser = new User { Id = userId, Name = "MaxUser" };
            
            _userRepositoryMock
                .Setup(repo => repo.GetByIdAsync(userId))
                .ReturnsAsync(expectedUser);

            // Act
            var result = await _userService.GetUserAsync(userId);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().Be(userId);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-9999)]
        public async Task GetUserAsync_WithInvalidId_ThrowsArgumentOutOfRangeException(int invalidId)
        {
            // Act
            Func<Task> act = async () => await _userService.GetUserAsync(invalidId);
            
            // Assert
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
                .WithParameterName("id");

            _userRepositoryMock.Verify(repo => repo.GetByIdAsync(It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public async Task GetUserAsync_WithNonExistingId_ReturnsNull()
        {
            // Arrange
            int userId = 99;
            _userRepositoryMock
                .Setup(repo => repo.GetByIdAsync(userId))
                .ReturnsAsync((User)null);

            // Act
            var result = await _userService.GetUserAsync(userId);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetUserAsync_WhenRepositoryThrows_PropagatesException()
        {
            // Arrange
            int userId = 1;
            _userRepositoryMock
                .Setup(repo => repo.GetByIdAsync(userId))
                .ThrowsAsync(new Exception("Database connection failed"));

            // Act
            Func<Task> act = async () => await _userService.GetUserAsync(userId);

            // Assert
            await act.Should().ThrowAsync<Exception>()
                .WithMessage("Database connection failed");
        }

        #endregion

        #region UpdateUserNameAsync Tests

        [Fact]
        public async Task UpdateUserNameAsync_WithValidParameters_UpdatesUserSuccessfully()
        {
            // Arrange
            int userId = 42;
            string newName = "Bob";
            var existingUser = new User { Id = userId, Name = "Alice" };

            _userRepositoryMock
                .Setup(repo => repo.GetByIdAsync(userId))
                .ReturnsAsync(existingUser);

            _userRepositoryMock
                .Setup(repo => repo.UpdateAsync(It.IsAny<User>()))
                .Returns(Task.CompletedTask);

            // Act
            await _userService.UpdateUserNameAsync(userId, newName);

            // Assert
            existingUser.Name.Should().Be(newName);
            _userRepositoryMock.Verify(repo => repo.UpdateAsync(It.Is<User>(u => u.Id == userId && u.Name == newName)), Times.Once);
        }

        [Fact]
        public async Task UpdateUserNameAsync_WithLongButValidName_UpdatesSuccessfully()
        {
            // Arrange
            int userId = 42;
            string longName = new string('A', 100);
            var existingUser = new User { Id = userId, Name = "Alice" };

            _userRepositoryMock
                .Setup(repo => repo.GetByIdAsync(userId))
                .ReturnsAsync(existingUser);

            _userRepositoryMock
                .Setup(repo => repo.UpdateAsync(It.IsAny<User>()))
                .Returns(Task.CompletedTask);

            // Act
            await _userService.UpdateUserNameAsync(userId, longName);

            // Assert
            existingUser.Name.Should().Be(longName);
            _userRepositoryMock.Verify(repo => repo.UpdateAsync(It.Is<User>(u => u.Id == userId && u.Name == longName)), Times.Once);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task UpdateUserNameAsync_WithInvalidId_ThrowsArgumentOutOfRangeException(int invalidId)
        {
            // Act
            Func<Task> act = async () => await _userService.UpdateUserNameAsync(invalidId, "ValidName");

            // Assert
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
                .WithParameterName("id");

            _userRepositoryMock.Verify(repo => repo.GetByIdAsync(It.IsAny<int>()), Times.Never);
            _userRepositoryMock.Verify(repo => repo.UpdateAsync(It.IsAny<User>()), Times.Never);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(null)]
        public async Task UpdateUserNameAsync_WithNullOrEmptyName_ThrowsArgumentException(string invalidName)
        {
            // Act
            Func<Task> act = async () => await _userService.UpdateUserNameAsync(1, invalidName);

            // Assert
            await act.Should().ThrowAsync<ArgumentException>();
            _userRepositoryMock.Verify(repo => repo.UpdateAsync(It.IsAny<User>()), Times.Never);
        }

        [Fact]
        public async Task UpdateUserNameAsync_WhenUserDoesNotExist_ThrowsKeyNotFoundException()
        {
            // Arrange
            int userId = 99;
            _userRepositoryMock
                .Setup(repo => repo.GetByIdAsync(userId))
                .ReturnsAsync((User)null);

            // Act
            Func<Task> act = async () => await _userService.UpdateUserNameAsync(userId, "NewName");

            // Assert
            await act.Should().ThrowAsync<KeyNotFoundException>();
            _userRepositoryMock.Verify(repo => repo.UpdateAsync(It.IsAny<User>()), Times.Never);
        }

        [Fact]
        public async Task UpdateUserNameAsync_WhenRepositorySaveFails_PropagatesException()
        {
            // Arrange
            int userId = 42;
            string newName = "Bob";
            var existingUser = new User { Id = userId, Name = "Alice" };

            _userRepositoryMock
                .Setup(repo => repo.GetByIdAsync(userId))
                .ReturnsAsync(existingUser);

            _userRepositoryMock
                .Setup(repo => repo.UpdateAsync(It.IsAny<User>()))
                .ThrowsAsync(new Exception("Database save failed"));

            // Act
            Func<Task> act = async () => await _userService.UpdateUserNameAsync(userId, newName);

            // Assert
            await act.Should().ThrowAsync<Exception>()
                .WithMessage("Database save failed");
        }

        #endregion
    }
}