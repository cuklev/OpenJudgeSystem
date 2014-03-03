﻿namespace OJS.Web.Areas.Contests.Controllers
{
    using System;
    using System.Data.Entity;
    using System.Linq;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Web;
    using System.Web.Mvc;

    using Kendo.Mvc.Extensions;
    using Kendo.Mvc.UI;

    using OJS.Common;
    using OJS.Common.Models;
    using OJS.Data;
    using OJS.Data.Models;
    using OJS.Web.Areas.Contests.Helpers;
    using OJS.Web.Areas.Contests.Models;
    using OJS.Web.Areas.Contests.ViewModels;
    using OJS.Web.Common.Extensions;
    using OJS.Web.Controllers;

    using Resource = Resources.Areas.Contests;

    public class CompeteController : BaseController
    {
        public const string CompeteUrl = "Compete";
        public const string PracticeUrl = "Practice";

        public CompeteController(IOjsData data)
            : base(data)
        {
        }

        public CompeteController(IOjsData data, UserProfile userProfile)
            : base(data, userProfile)
        {
        }

        /// <summary>
        /// Validates if a contest is correctly found. If the user wants to practice or compete in the contest
        /// checks if the contest can be practiced or competed.
        /// </summary>
        /// <param name="contest">Contest to validate.</param>
        /// <param name="official">A flag checking if the contest will be practiced or competed</param>
        [NonAction]
        public static void ValidateContest(Contest contest, bool official)
        {
            if (contest == null || contest.IsDeleted || !contest.IsVisible)
            {
                throw new HttpException((int)HttpStatusCode.NotFound, Resource.ContestsGeneral.Contest_not_found);
            }

            if (official && !contest.CanBeCompeted)
            {
                throw new HttpException((int)HttpStatusCode.Forbidden, Resource.ContestsGeneral.Contest_cannot_be_competed);
            }

            if (!official && !contest.CanBePracticed)
            {
                throw new HttpException((int)HttpStatusCode.Forbidden, Resource.ContestsGeneral.Contest_cannot_be_practiced);
            }
        }

        /// <summary>
        /// Displays user compete information: tasks, send source form, ranking, submissions, ranking, etc.
        /// Users only.
        /// </summary>
        [Authorize]
        public ActionResult Index(int id, bool official)
        {
            var contest = this.Data.Contests.GetById(id);
            ValidateContest(contest, official);

            var participantFound = this.Data.Participants.Any(id, this.UserProfile.Id, official);

            if (!participantFound)
            {
                if (!contest.ShouldShowRegistrationForm(official))
                {
                    this.Data.Participants.Add(new Participant(id, this.UserProfile.Id, official));
                    this.Data.SaveChanges();
                }
                else
                {
                    // Participant not found, the contest requires password or the contest has questions
                    // to be answered before registration. Redirect to the registration page.
                    // The registration page will take care of all security checks.
                    return this.RedirectToAction("Register", new { id, official });
                }
            }

            var participant = this.Data.Participants.GetWithContest(id, this.UserProfile.Id, official);
            var participantViewModel = new ParticipantViewModel(participant, official);

            this.ViewBag.CompeteType = official ? CompeteUrl : PracticeUrl;

            return this.View(participantViewModel);
        }

        /// <summary>
        /// Displays form for contest registration.
        /// Users only.
        /// </summary>
        [HttpGet, Authorize]
        public ActionResult Register(int id, bool official)
        {
            var participantFound = this.Data.Participants.Any(id, this.UserProfile.Id, official);
            if (participantFound)
            {
                // Participant exists. Redirect to index page.
                return this.RedirectToAction(GlobalConstants.Index, new { id, official });
            }

            var contest = this.Data.Contests.All().Include(x => x.Questions).FirstOrDefault(x => x.Id == id);

            ValidateContest(contest, official);

            if (contest.ShouldShowRegistrationForm(official))
            {
                var contestRegistrationModel = new ContestRegistrationViewModel(contest, official);
                return this.View(contestRegistrationModel);
            }

            var participant = new Participant(id, this.UserProfile.Id, official);
            this.Data.Participants.Add(participant);
            this.Data.SaveChanges();

            return this.RedirectToAction(GlobalConstants.Index, new { id, official });
        }

        /// <summary>
        /// Accepts form input for contest registration.
        /// Users only.
        /// </summary>
        //// TODO: Refactor
        [HttpPost, Authorize]
        public ActionResult Register(bool official, ContestRegistrationModel registrationData)
        {
            // check if the user has already registered for participation and redirect him to the correct action
            var participantFound = this.Data.Participants.Any(registrationData.ContestId, this.UserProfile.Id, official);

            if (participantFound)
            {
                return this.RedirectToAction(GlobalConstants.Index, new { id = registrationData.ContestId, official });
            }

            var contest = this.Data.Contests.GetById(registrationData.ContestId);
            ValidateContest(contest, official);

            if (official && contest.HasContestPassword)
            {
                if (string.IsNullOrEmpty(registrationData.Password))
                {
                    this.ModelState.AddModelError("Password", Resource.Views.CompeteRegister.Empty_Password);
                }
                else if (contest.ContestPassword != registrationData.Password)
                {
                    this.ModelState.AddModelError("Password", Resource.Views.CompeteRegister.Incorrect_password);
                }
            }

            if (!official && contest.HasPracticePassword)
            {
                if (string.IsNullOrEmpty(registrationData.Password))
                {
                    this.ModelState.AddModelError("Password", Resource.Views.CompeteRegister.Empty_Password);
                }
                else if (contest.PracticePassword != registrationData.Password)
                {
                    this.ModelState.AddModelError("Password", Resource.Views.CompeteRegister.Incorrect_password);
                }
            }

            var questionsToAnswerCount = official ?
                contest.Questions.Count(x => x.AskOfficialParticipants) :
                contest.Questions.Count(x => x.AskPracticeParticipants);

            if (questionsToAnswerCount != registrationData.Questions.Count())
            {
                this.ModelState.AddModelError("Questions", Resource.Views.CompeteRegister.Not_all_questions_answered);
            }

            var contestQuestions = contest.Questions.ToList();

            var participant = new Participant(registrationData.ContestId, this.UserProfile.Id, official);
            this.Data.Participants.Add(participant);
            var counter = 0;
            foreach (var question in registrationData.Questions)
            {
                var contestQuestion = contestQuestions.FirstOrDefault(x => x.Id == question.QuestionId);

                var regularExpression = contestQuestion.RegularExpressionValidation;
                bool correctlyAnswered = false;

                if (!string.IsNullOrEmpty(regularExpression))
                {
                    correctlyAnswered = Regex.IsMatch(question.Answer, regularExpression);
                }

                if (contestQuestion.Type == ContestQuestionType.DropDown)
                {
                    int contestAnswerId;
                    if (int.TryParse(question.Answer, out contestAnswerId) && contestQuestion.Answers.Any(x => x.Id == contestAnswerId))
                    {
                        correctlyAnswered = true;
                    }

                    if (!correctlyAnswered)
                    {
                        this.ModelState.AddModelError(string.Format("Questions[{0}].Answer", counter), "Invalid selection");
                    }
                }

                participant.Answers.Add(new ParticipantAnswer
                                                {
                                                    ContestQuestionId = question.QuestionId,
                                                    Answer = question.Answer
                                                });

                counter++;
            }

            if (!this.ModelState.IsValid)
            {
                return this.View(new ContestRegistrationViewModel(contest, registrationData, official));
            }

            this.Data.SaveChanges();

            return this.RedirectToAction(GlobalConstants.Index, new { id = registrationData.ContestId, official });
        }

        /// <summary>
        /// Processes a participant's submission for a problem.
        /// </summary>
        /// <param name="participantSubmission">Participant submission.</param>
        /// <param name="official">A check whether the contest is official or practice.</param>
        /// <returns>Returns confirmation if the submission was correctly processed.</returns>
        [HttpPost, Authorize]
        public ActionResult Submit(SubmissionModel participantSubmission, bool official)
        {
            var problem = this.Data.Problems.All().FirstOrDefault(x => x.Id == participantSubmission.ProblemId);
            if (problem == null)
            {
                throw new HttpException((int)HttpStatusCode.Unauthorized, Resource.ContestsGeneral.Problem_not_found);
            }

            var participant = this.Data.Participants.GetWithContest(problem.ContestId, this.UserProfile.Id, official);
            if (participant == null)
            {
                throw new HttpException((int)HttpStatusCode.Unauthorized, Resource.ContestsGeneral.User_is_not_registered_for_exam);
            }

            ValidateContest(participant.Contest, official);

            var lastSubmission =
                this.Data.Submissions.All()
                    .Where(x => x.ParticipantId == participant.Id)
                    .OrderBy(x => x.CreatedOn)
                    .Select(x => new { x.Id, x.CreatedOn })
                    .FirstOrDefault();
            if (lastSubmission != null)
            {
                // check if the submission was sent after the submission time limit has passed
                var latestSubmissionTime = lastSubmission.CreatedOn;
                var differenceBetweenSubmissions = DateTime.Now - latestSubmissionTime;
                int limitBetweenSubmissions = participant.Contest.LimitBetweenSubmissions;
                if (differenceBetweenSubmissions.TotalSeconds < limitBetweenSubmissions)
                {
                    throw new HttpException((int)HttpStatusCode.ServiceUnavailable, Resource.ContestsGeneral.Submission_was_sent_too_soon);
                }
            }

            if (problem.SourceCodeSizeLimit < participantSubmission.Content.Length)
            {
                throw new HttpException((int)HttpStatusCode.BadRequest, Resource.ContestsGeneral.Submission_too_long);
            }

            if (!this.ModelState.IsValid)
            {
                throw new HttpException((int)HttpStatusCode.BadRequest, Resource.ContestsGeneral.Invalid_request);
            }

            this.Data.Submissions.Add(new Submission
            {
                ContentAsString = participantSubmission.Content,
                ProblemId = participantSubmission.ProblemId,
                SubmissionTypeId = participantSubmission.SubmissionTypeId,
                ParticipantId = participant.Id
            });

            this.Data.SaveChanges();

            return this.Json(participantSubmission.ProblemId);
        }

        public ActionResult SubmitBinaryFile(BinarySubmissionModel participantSubmission, bool official)
        {
            // TODO: Validate participant
            // TODO: Validate if binary files allowed?
            // TODO: Validate file extension
            // TODO: Validate file size
            throw new NotImplementedException("SubmitBinaryFile is not implemented, yet.");
            
            //// this.Data.Submissions.Add(new Submission
            //// {
            ////     Content = participantSubmission.Content,
            ////     ProblemId = participantSubmission.ProblemId,
            ////     SubmissionTypeId = participantSubmission.SubmissionTypeId,
            ////     ParticipantId = participant.Id
            //// });
            
            this.Data.SaveChanges();

            return this.Json(participantSubmission.ProblemId);
        }

        /// <summary>
        /// Obtains the partial view for a particular problem.
        /// </summary>
        /// <param name="id">The problem Id</param>
        /// <param name="official">A check whether the problem is practiced or competed.</param>
        /// <returns>Returns a partial view with the problem information.</returns>
        [Authorize]
        public ActionResult Problem(int id, bool official)
        {
            this.ViewBag.IsOfficial = official;
            this.ViewBag.CompeteType = official ? CompeteUrl : PracticeUrl;

            var problem = this.Data.Problems.GetById(id);

            if (problem == null)
            {
                throw new HttpException((int)HttpStatusCode.NotFound, Resource.ContestsGeneral.Problem_not_found);
            }

            ValidateContest(problem.Contest, official);

            if (!this.Data.Participants.Any(problem.ContestId, this.UserProfile.Id, official))
            {
                return this.RedirectToAction("Register", new { id = problem.ContestId, official });
            }

            var problemViewModel = new ContestProblemViewModel(problem);

            return this.PartialView("_ProblemPartial", problemViewModel);
        }

        /// <summary>
        /// Gets a participant's submission results for a problem.
        /// </summary>
        /// <param name="request">The Kendo data source request.</param>
        /// <param name="id">The problem id.</param>
        /// <param name="official">A check whether the problem is practiced or competed.</param>
        /// <returns>Returns the submissions results for a participant's problem.</returns>
        [Authorize]
        public ActionResult ReadSubmissionResults([DataSourceRequest]DataSourceRequest request, int id, bool official)
        {
            var problem = this.Data.Problems.GetById(id);
            var participant = this.Data.Participants.GetWithContest(problem.ContestId, this.UserProfile.Id, official);

            if (participant == null)
            {
                throw new HttpException((int)HttpStatusCode.Unauthorized, Resource.ContestsGeneral.User_is_not_registered_for_exam);
            }

            if (!problem.ShowResults)
            {
                throw new HttpException((int)HttpStatusCode.Forbidden, Resource.ContestsGeneral.Problem_results_not_available);
            }

            var userSubmissions = this.Data.Submissions.All()
                                                            .Where(x =>
                                                                    x.ProblemId == id &&
                                                                    x.ParticipantId == participant.Id)
                                                            .Select(SubmissionResultViewModel.FromSubmission);

            return this.Json(userSubmissions.ToDataSourceResult(request));
        }

        [Authorize]
        public ActionResult ReadSubmissionResultsAreCompiled([DataSourceRequest]DataSourceRequest request, int id, bool official)
        {
            var problem = this.Data.Problems.GetById(id);
            var participant = this.Data.Participants.GetWithContest(problem.ContestId, this.UserProfile.Id, official);

            if (participant == null)
            {
                throw new HttpException((int)HttpStatusCode.Unauthorized, Resource.ContestsGeneral.User_is_not_registered_for_exam);
            }

            var userSubmissions = this.Data.Submissions.All()
                                                            .Where(x =>
                                                                    x.ProblemId == id &&
                                                                    x.ParticipantId == participant.Id)
                                                            .Select(SubmissionResultIsCompiledViewModel.FromSubmission);

            return this.Json(userSubmissions.ToDataSourceResult(request));
        }

        /// <summary>
        /// Gets the allowed submission types for a contest.
        /// </summary>
        /// <param name="id">The contest id.</param>
        /// <returns>Returns the allowed submission types as JSON.</returns>
        public ActionResult GetAllowedSubmissionTypes(int id)
        {
            var contest = this.Data.Contests.GetById(id);

            if (contest == null)
            {
                throw new HttpException((int)HttpStatusCode.NotFound, Resource.ContestsGeneral.Contest_not_found);
            }

            var submissionTypesSelectListItems = contest
                                                    .SubmissionTypes
                                                    .ToList()
                                                    .Select(x => new SelectListItem
                                                    {
                                                        Text = x.Name,
                                                        Value = x.Id.ToString(),
                                                        Selected = x.IsSelectedByDefault
                                                    });

            return this.Json(submissionTypesSelectListItems, JsonRequestBehavior.AllowGet);
        }

        /// <summary>
        /// Gets a problem resource and sends it to the user. If the user is not logged in redirects him to the 
        /// login page. If the user is not registered for the exam - redirects him to the appropriate page.
        /// </summary>
        /// <param name="id">The resource id.</param>
        /// <param name="official">A check whether the problem is practiced or competed.</param>
        /// <returns>Returns a file with the resource contents or redirects the user to the appropriate
        /// registration page.</returns>
        public ActionResult DownloadResource(int id, bool official)
        {
            var problemWithResource = this.Data.Problems
                                                    .All()
                                                    .FirstOrDefault(problem =>
                                                            problem.Resources.Any(res => res.Id == id && !res.IsDeleted));

            if (problemWithResource == null)
            {
                throw new HttpException((int)HttpStatusCode.NotFound, Resource.ContestsGeneral.Problem_not_found);
            }

            var contest = problemWithResource.Contest;
            bool userCanDownloadResource = false;

            if (this.UserProfile == null)
            {
                ValidateContest(contest, official);
            }
            else if (this.User != null && this.User.IsAdmin())
            {
                // TODO: add unit tests
                // If the user is an administrator he can download the resource at any time.
                userCanDownloadResource = true;
            }
            else
            {
                ValidateContest(contest, official);
                userCanDownloadResource = this.Data.Participants.Any(contest.Id, this.UserProfile.Id, official);
            }

            if (userCanDownloadResource ||
                (contest.CanBeCompeted && !contest.HasContestPassword) ||
                (contest.CanBePracticed && !contest.HasPracticePassword))
            {
                var resource = problemWithResource.Resources.FirstOrDefault(res => res.Id == id);

                if (string.IsNullOrWhiteSpace(resource.FileExtension) || resource.File == null || resource.File.Length == 0)
                {
                    throw new HttpException((int)HttpStatusCode.Forbidden, Resource.ContestsGeneral.Resource_cannot_be_downloaded);
                }

                return this.File(resource.File, "application/octet-stream", string.Format("{0}_{1}.{2}", resource.Problem.Name, resource.Name, resource.FileExtension));
            }

            if ((contest.CanBePracticed && !official) || (contest.CanBeCompeted && official))
            {
                return this.RedirectToAction("Register", new { official, id = contest.Id });
            }

            throw new HttpException((int)HttpStatusCode.Forbidden, Resource.ContestsGeneral.Resource_cannot_be_downloaded);
        }

        /// <summary>
        /// Gets the content of a participant submission for a particular problem.
        /// </summary>
        /// <param name="id">The submission id.</param>
        /// <returns>Returns a JSON with the submission content.</returns>
        //// TODO: Remove if not used
        [Authorize]
        public ActionResult GetSubmissionContent(int id)
        {
            var submission = this.Data.Submissions.All().FirstOrDefault(x => x.Id == id);

            if (submission == null)
            {
                throw new HttpException((int)HttpStatusCode.NotFound, Resource.ContestsGeneral.Submission_not_found);
            }

            if (submission.Participant.UserId != this.UserProfile.Id)
            {
                throw new HttpException((int)HttpStatusCode.Forbidden, Resource.ContestsGeneral.Submission_not_made_by_user);
            }

            var contentString = submission.ContentAsString;

            return this.Json(contentString, JsonRequestBehavior.AllowGet);
        }
    }
}
