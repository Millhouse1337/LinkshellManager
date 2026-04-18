using System.Drawing;
using System.Globalization;
using LinkshellManager.Data;
using LinkshellManager.Interfaces;
using LinkshellManager.Models;
using LinkshellManager.Repository;
using LinkshellManager.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;


namespace LinkshellManager.Controllers
{
    public class AdminController : Controller
    {
        private readonly IUserRepository _userRepository;

        public AdminController(IUserRepository userRepository)
        {
            _userRepository = userRepository;
        }

        [Authorize(Roles = UserRoles.Admin)]
        public async Task<IActionResult> Index()
        {
            var users = await _userRepository.GetAllUsers();
            var viewModel = new AdminViewModel { Users = users };
            return View(viewModel);
        }

    }
}
