This application uses a microservice architecture between
two services (InventoryService \& OrdersService) and a
SQL Express database. The application is split into two 

services to increase independence and modularity between 

separate processes. For instance, InventoryService can be 

run without OrdersService and vice-versa. This is advantageous for

CI/CD since a fix at one module does not require a change for all 

of the program like in monolith designs. This also isolates

any issues that may arise in one service from another.



Swagger is used as a GUI for simplifying requests when testing 

the application by hand (typically, Swagger would be dev only. 

But, for the purposes of this project, Swagger is always enabled 

to make HTTP calls simplified.)



Additionally, the solution contains two projects for running
tests: IntegrationTests and OrdersService.Tests, with the latter
being unit tests for OrdersService.



Prerequisites: 



To run with Docker:

* Docker Desktop only.



To run locally:

* .NET 8 SDK + SQL Server Express



To run tests:

* .NET 8 SDK (no Docker or SQL Server required)



How to setup the application:

* git clone <repo http link> in a chosen directory
* cd into the repo's directory with a terminal (command prompt, powershell, bash)
* create a .env file at the root of the solution following the provided example file, ".env.example"

  * InventoryApiKey can be any string.
  * MSSQL\_SA\_PASSWORD must be 8+ characters with 3 of the following: uppercase, lowercase, digit, and symbol (this password is only used in Docker).
  * Add your SQL Express connection string to appsettings.json for both services.

    * Example:     
    * "OrdersDb": "Server=localhost\\\\SQLEXPRESS;Database=OrdersDb;Trusted\_Connection=True;TrustServerCertificate=True;Encrypt=False"



How to run application locally:

* Have two terminals open at the solution root
* In terminal 1: cd InventoryService; dotnet run
* In terminal 2: cd OrdersService; dotnet run
* SWAGGER:

  * open a browser to access the endpoints with swagger

    * InventoryService: http://localhost:5209/swagger
    * OrdersService: http://localhost:5210/swagger
  * In Swagger, first click the "Authorize" button at the top-right of the page.

    * Paste the InventoryApiKey (the one you created in .env)
  * Swagger can now be used to easily send HTTP requests between services.



How to run application with Docker:

* run command: docker compose up -d --build

  * 3 containers should be running: sqlserver, inventoryservice, ordersservice
* SWAGGER:

  * open a browser to access the endpoints with swagger

    * InventoryService: http://localhost:5209/swagger
    * OrdersService: http://localhost:5210/swagger
  * In Swagger, first click the "Authorize" button at the top-right of the page.

    * Paste the InventoryApiKey (the one you created in .env)
  * Swagger can now be used to easily send HTTP requests between services. 



How to run tests:

* dotnet test



Unit tests:

* Validate OrdersService functionality for orders being confirmed,
rejected, and product not found.

Integration tests:

* The tests validate successful operation between
InventoryService and OrdersService.
* Test 1: On success, confirms an order can be confirmed
and the product quantity is correctly decremented.
* Test 2: On success, confirms an order placed with
inadequate inventory is rejected and product quantity
is unaffected.

