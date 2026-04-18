using System.ComponentModel.DataAnnotations;

namespace LinkshellManager.ViewModels
{
    // ViewModel for Forgot Password
    public class ForgotPasswordViewModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; }
    }
}