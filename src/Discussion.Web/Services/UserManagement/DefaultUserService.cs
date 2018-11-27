using System;
using System.Linq;
using System.Threading.Tasks;
using Discussion.Core.Communication.Email;
using Discussion.Core.Data;
using Discussion.Core.Models;
using Discussion.Core.Utilities;
using Discussion.Web.Services.UserManagement.EmailConfirmation;
using Discussion.Web.Services.UserManagement.Exceptions;
using Discussion.Web.Services.UserManagement.PhoneNumberVerification;
using Discussion.Web.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Discussion.Web.Services.UserManagement
{
    public class DefaultUserService : IUserService
    {
        private readonly IRepository<User> _userRepo;
        private readonly UserManager<User> _userManager;
        private readonly IUrlHelper _urlHelper;
        private readonly IEmailDeliveryMethod _emailDeliveryMethod;
        private readonly IConfirmationEmailBuilder _confirmationEmailBuilder;
        private readonly IPhoneNumberVerificationService _phoneNumberVerificationService;
        private readonly IRepository<VerifiedPhoneNumber> _verifiedPhoneNumberRepo;

        public DefaultUserService(IRepository<User> userRepo, 
            UserManager<User> userManager, 
            IEmailDeliveryMethod emailDeliveryMethod, 
            IUrlHelper urlHelper, 
            IConfirmationEmailBuilder confirmationEmailBuilder, 
            IPhoneNumberVerificationService phoneNumberVerificationService, 
            IRepository<VerifiedPhoneNumber> verifiedPhoneNumberRepo)
        {
            _userRepo = userRepo;
            _userManager = userManager;
            _emailDeliveryMethod = emailDeliveryMethod;
            _urlHelper = urlHelper;
            _confirmationEmailBuilder = confirmationEmailBuilder;
            _phoneNumberVerificationService = phoneNumberVerificationService;
            _verifiedPhoneNumberRepo = verifiedPhoneNumberRepo;
        }

        public async Task<IdentityResult> UpdateUserInfoAsync(User user, UserSettingsViewModel userSettingsViewModel)
        {
            var updateEmailResult = await UpdateEmail(userSettingsViewModel, user);
            if (!updateEmailResult.Succeeded)
            {
                return updateEmailResult;
            }
            
            user.AvatarFileId = userSettingsViewModel.AvatarFileId;
            user.DisplayName = userSettingsViewModel.DisplayName;
            if (string.IsNullOrWhiteSpace(user.DisplayName))
            {
                user.DisplayName = user.UserName;
            }

            _userRepo.Update(user);
            return IdentityResult.Success;
        }

        private async Task<IdentityResult> UpdateEmail(UserSettingsViewModel userSettingsViewModel, User user)
        {
            var existingEmail = user.EmailAddress?.Trim();
            var newEmail = userSettingsViewModel.EmailAddress?.Trim();
            
            var emailNotChanged = existingEmail.IgnoreCaseEqual(newEmail);
            if (emailNotChanged)
            {
                return IdentityResult.Success;
            }
            
            var emailTaken = IsEmailTakenByAnotherUser(user.Id, newEmail);
            
            return emailTaken 
                ? EmailTakenResult()
                : await _userManager.SetEmailAsync(user, newEmail);
        }

        public async Task SendEmailConfirmationMailAsync(User user, string urlProtocol)
        {
            if (user.EmailAddressConfirmed)
            {
                throw new UserEmailAlreadyConfirmedException();
            }
            
            var tokenString = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var tokenInEmail = new UserEmailToken {UserId = user.Id, Token = tokenString};
            
            // ReSharper disable Mvc.ActionNotResolved
            // ReSharper disable Mvc.ControllerNotResolved
            var callbackUrl = _urlHelper.Action(
                "ConfirmEmail",
                "User",
                new {token = tokenInEmail.EncodeAsUrlQueryString()},
                protocol: urlProtocol);

            var emailBody = _confirmationEmailBuilder.BuildEmailBody(callbackUrl);
            await _emailDeliveryMethod.SendEmailAsync(user.EmailAddress, "dotnet club 用户邮件地址确认", emailBody);
        }

        public async Task<IdentityResult> ConfirmEmailAsync(User user, UserEmailToken tokenInEmail)
        {
            if (IsEmailTakenByAnotherUser(user.Id, user.EmailAddress))
            {
                return EmailTakenResult();
            }
            
            var identityResult = await _userManager.ConfirmEmailAsync(user, tokenInEmail.Token);
            return identityResult.Succeeded
                ? await _userManager.UpdateAsync(user)
                : identityResult;
        }

        public async Task SendPhoneNumberVerificationCodeAsync(User user, string phoneNumber)
        {
            if (!user.CanModifyPhoneNumberNow() || 
                _phoneNumberVerificationService.IsFrequencyExceededForUser(user.Id))
            {
                throw new PhoneNumberVerificationFrequencyExceededException();
            }

            await _phoneNumberVerificationService.SendVerificationCodeAsync(user.Id, phoneNumber);
        }

        public void VerifyPhoneNumberByCode(User user, string verificationCode)
        {
            var validatedPhoneNumber = _phoneNumberVerificationService.GetVerifiedPhoneNumberByCode(user.Id, verificationCode);
            if (validatedPhoneNumber == null)
            {
                throw new PhoneNumberVerificationCodeInvalidException();
            }

            if (user.VerifiedPhoneNumber == null)
            {
                user.VerifiedPhoneNumber = new VerifiedPhoneNumber{ PhoneNumber =  validatedPhoneNumber };
                _verifiedPhoneNumberRepo.Save(user.VerifiedPhoneNumber);
            }
            else
            {
                user.VerifiedPhoneNumber.PhoneNumber = validatedPhoneNumber;
                user.VerifiedPhoneNumber.ModifiedAtUtc = DateTime.UtcNow;
                _verifiedPhoneNumberRepo.Update(user.VerifiedPhoneNumber);
            }
            _userRepo.Update(user);
        }

        private bool IsEmailTakenByAnotherUser(int thisUserId, string checkingEmail)
        {
            if (string.IsNullOrWhiteSpace(checkingEmail))
            {
                return false;
            }
            
            return _userRepo.All()
                .Any(u => u.EmailAddressConfirmed
                          && u.Id != thisUserId
                          && u.EmailAddress != null
                          && u.EmailAddress.ToLower() == checkingEmail.ToLower());
        }

        private static IdentityResult EmailTakenResult()
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "EmailTaken",
                Description = "邮件地址已由其他用户使用"
            });
        }
    }
}