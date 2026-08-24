using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace {{AppNamespace}}.Identity.Migrations
{
    /// <summary>
    /// One column: whether this account is still on a password somebody else chose.
    ///
    /// <para>Its own migration rather than an edit to the initial one, because the initial one has
    /// already run on every database that exists. Amending a migration that has been applied leaves
    /// the table without the column and the history saying it has it, and the failure surfaces later
    /// as a query against a column that is not there.</para>
    /// </summary>
    public partial class UserManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // False for everyone who already exists. They chose their own password at setup, so
            // defaulting to true would lock every current account into a change-password screen for
            // a password there is nothing wrong with.
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "AspNetUsers");
        }
    }
}
